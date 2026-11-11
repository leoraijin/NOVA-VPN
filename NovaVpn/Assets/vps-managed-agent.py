# NOVA managed VPS transaction agent. Linux/Python3/systemd only.
# Transport/UI activation remains gated until native Linux integration is verified.
import base64
import contextlib
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys

ROOT = Path('/var/lib/nova-vpn')
UNIT_PATH = Path('/etc/systemd/system/nova-vpn-managed.service')
UNIT_NAME = 'nova-vpn-managed.service'
MAX_BINARY = 64 * 1024 * 1024
MAX_CONFIG = 65536
MAX_REQUEST = 96 * 1024 * 1024
TERMINAL = ('committed', 'rolled-back')

LAUNCHER = b'''#!/usr/bin/python3
import hashlib,json,os,re,stat
from pathlib import Path
root=Path('/var/lib/nova-vpn')
def read(path,limit):
    info=path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or info.st_nlink!=1 or info.st_mode&0o077 or info.st_size>limit:
        raise RuntimeError('Untrusted NOVA payload')
    return path.read_bytes()
current=json.loads(read(root/'current.json',8192))
owner=json.loads(read(root/'owner.json',8192))
generation=current['generation']
if not re.fullmatch('[0-9a-f]{32}',generation): raise RuntimeError('Invalid generation')
folder=root/'generations'/generation
manifest=json.loads(read(folder/'manifest.json',8192))
if manifest['generation']!=generation or manifest['installation']!=owner['installation']:
    raise RuntimeError('Foreign generation')
for name,field,limit in [('sing-box','binary_sha256',67108864),('config.json','config_sha256',65536)]:
    if hashlib.sha256(read(folder/name,limit)).hexdigest()!=manifest[field]:
        raise RuntimeError('Changed NOVA payload')
os.execv(str(folder/'sing-box'),[str(folder/'sing-box'),'run','-c',str(folder/'config.json')])
'''
UNIT = b'''[Unit]
Description=NOVA managed VPN server
After=network-online.target
Wants=network-online.target
[Service]
Type=exec
ExecStart=/usr/bin/python3 /var/lib/nova-vpn/launcher.py
Restart=on-failure
RestartSec=3
TimeoutStopSec=15
KillMode=control-group
UMask=0077
NoNewPrivileges=yes
PrivateTmp=yes
PrivateDevices=yes
ProtectSystem=strict
ProtectHome=yes
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
[Install]
WantedBy=multi-user.target
'''

class VpsError(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def require(ok, code='invalid_request'):
    if not ok:
        raise VpsError(code)


def hex_id(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{32}', value) is not None


def digest(value):
    return hashlib.sha256(value).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True, allow_nan=False).encode('ascii')


def object_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate_json_key')
        result[key] = value
    return result


def parse_json(data):
    try:
        return json.loads(data, object_pairs_hook=object_pairs, parse_constant=lambda _: (_ for _ in ()).throw(VpsError('invalid_json')))
    except (ValueError, UnicodeError):
        raise VpsError('invalid_json')


def decode(value, maximum):
    require(isinstance(value, str) and len(value) <= ((maximum + 2) // 3) * 4)
    try:
        data = base64.b64decode(value, validate=True)
    except (ValueError, TypeError):
        raise VpsError('invalid_base64')
    require(len(data) <= maximum and base64.b64encode(data).decode('ascii') == value)
    return data


def validate_pointer(value):
    if value is None:
        return None
    require(isinstance(value, dict) and set(value) == {'schema', 'revision', 'generation', 'previous', 'operation', 'request_sha256'}, 'invalid_pointer')
    require(type(value['schema']) is int and value['schema'] == 1 and type(value['revision']) is int and 1 <= value['revision'] <= 2147483647, 'invalid_pointer')
    require(hex_id(value['generation']) and hex_id(value['operation']) and (value['previous'] is None or hex_id(value['previous'])), 'invalid_pointer')
    require(isinstance(value['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', value['request_sha256']), 'invalid_pointer')
    return value


def validate_config(data):
    config = parse_json(data)
    require(isinstance(config, dict) and set(config) == {'log', 'inbounds', 'outbounds'}, 'unsupported_config')
    require(config['log'] == {'level': 'warn'}, 'unsupported_config')
    require(config['outbounds'] == [{'type': 'direct', 'tag': 'direct'}], 'unsupported_config')
    require(isinstance(config['inbounds'], list) and len(config['inbounds']) == 1, 'unsupported_config')
    inbound = config['inbounds'][0]
    require(isinstance(inbound, dict) and set(inbound) == {'type', 'tag', 'listen', 'listen_port', 'method', 'password'}, 'unsupported_config')
    require(inbound['type'] == 'shadowsocks' and inbound['tag'] == 'nova-vps-in' and inbound['listen'] == '::' and inbound['method'] == '2022-blake3-aes-256-gcm', 'unsupported_config')
    require(type(inbound['listen_port']) is int and 1 <= inbound['listen_port'] <= 65535, 'unsupported_config')
    require(len(decode(inbound['password'], 32)) == 32, 'unsupported_config')


class LinuxOps:
    def check_platform(self):
        require(sys.platform.startswith('linux') and os.geteuid() == 0, 'linux_root_required')
        require(Path('/run/systemd/system').is_dir() and Path('/usr/bin/python3').is_file() and Path('/usr/bin/systemctl').is_file(), 'systemd_required')

    def check_path(self, path, directory=False, mode=None):
        info = path.lstat()
        require((stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)) and info.st_uid == 0 and not info.st_mode & 0o022, 'untrusted_path')
        if not directory:
            require(info.st_nlink == 1, 'untrusted_path')
        if mode is not None:
            require(stat.S_IMODE(info.st_mode) == mode, 'untrusted_permissions')

    def check_parents(self, path):
        for parent in reversed(path.parents):
            self.check_path(parent, directory=True)

    def sync_directory(self, path):
        fd = os.open(str(path), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)

    @contextlib.contextmanager
    def lock(self, path):
        import fcntl
        fd = os.open(str(path), os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        try:
            self.check_path(path, mode=0o600)
            require(os.fstat(fd).st_ino == path.lstat().st_ino, 'changed_lock')
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise VpsError('operation_busy')
            yield
        finally:
            os.close(fd)

    @contextlib.contextmanager
    def read_lock(self, path):
        import fcntl
        try:
            fd = os.open(str(path), os.O_RDONLY | os.O_NOFOLLOW)
        except FileNotFoundError:
            raise VpsError('status_unavailable')
        try:
            self.check_path(path, mode=0o600)
            info = os.fstat(fd)
            require((info.st_dev, info.st_ino) == (path.lstat().st_dev, path.lstat().st_ino), 'changed_lock')
            try:
                fcntl.flock(fd, fcntl.LOCK_SH | fcntl.LOCK_NB)
            except BlockingIOError:
                raise VpsError('operation_busy')
            yield
        finally:
            os.close(fd)

    def command(self, *args):
        try:
            result = subprocess.run(['/usr/bin/systemctl', '--no-pager', *args], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=30,
                                    env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'SYSTEMD_COLORS': '0'})
        except (OSError, subprocess.TimeoutExpired):
            raise VpsError('service_command_failed')
        require(result.returncode == 0 and len(result.stdout) <= 8192, 'service_command_failed')
        return result.stdout.decode('ascii', 'strict')

    def service_state(self):
        text = self.command('show', '--all', '--property=LoadState,FragmentPath,ActiveState,UnitFileState,DropInPaths,NeedDaemonReload', UNIT_NAME)
        fields = {}
        for line in text.splitlines():
            key, separator, value = line.partition('=')
            require(separator and key not in fields, 'service_state_unknown')
            fields[key] = value
        require(set(fields) == {'LoadState', 'FragmentPath', 'ActiveState', 'UnitFileState', 'DropInPaths', 'NeedDaemonReload'}, 'service_state_unknown')
        require(fields['DropInPaths'] == '', 'foreign_service')
        require(fields['NeedDaemonReload'] in ('yes', 'no'), 'service_state_unknown')
        if fields['LoadState'] == 'not-found':
            require(fields['FragmentPath'] == '' and fields['ActiveState'] == 'inactive', 'service_state_unknown')
            return {'exists': False, 'active': False, 'enabled': False}
        require(fields['LoadState'] == 'loaded' and fields['FragmentPath'] == str(UNIT_PATH), 'foreign_service')
        require(fields['NeedDaemonReload'] == 'no', 'service_definition_pending_reload')
        require(fields['ActiveState'] in ('active', 'inactive', 'failed') and fields['UnitFileState'] in ('enabled', 'disabled'), 'service_state_unknown')
        return {'exists': True, 'active': fields['ActiveState'] == 'active', 'enabled': fields['UnitFileState'] == 'enabled'}

    def reload(self):
        self.command('daemon-reload')

    def stop(self):
        self.command('stop', UNIT_NAME)

    def set_service(self, active, enabled):
        self.command('enable' if enabled else 'disable', UNIT_NAME)
        self.command('restart' if active else 'stop', UNIT_NAME)
        state = self.service_state()
        require(state == {'exists': True, 'active': active, 'enabled': enabled}, 'service_readback_failed')

    def validate_binary(self, folder):
        machine = {'x86_64': 62, 'aarch64': 183}.get(os.uname().machine)
        header = (folder / 'sing-box').read_bytes()[:64]
        require(machine is not None and len(header) >= 64 and struct.unpack('<H', header[18:20])[0] == machine, 'architecture_mismatch')
        try:
            result = subprocess.run([str(folder / 'sing-box'), 'check', '-c', str(folder / 'config.json')], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=15,
                                    env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C'})
        except (OSError, subprocess.TimeoutExpired):
            raise VpsError('config_check_failed')
        require(result.returncode == 0, 'config_check_failed')


class Transaction:
    def __init__(self, installation, ops=None, root=ROOT, unit_path=UNIT_PATH, checkpoint=None):
        require(hex_id(installation))
        self.installation, self.ops = installation, ops or LinuxOps()
        self.root, self.unit = Path(root), Path(unit_path)
        self.checkpoint = checkpoint or (lambda phase: None)

    def _atomic(self, path, data, mode=0o600):
        require(isinstance(data, bytes))
        self.ops.check_parents(path)
        if os.path.lexists(path):
            self.ops.check_path(path, mode=mode)
        temp = path.with_name('.' + path.name + '.new')
        if os.path.lexists(temp):
            self.ops.check_path(temp, mode=mode)
            temp.unlink()
        fd = os.open(str(temp), os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), mode)
        with os.fdopen(fd, 'wb') as stream:
            if hasattr(os, 'fchmod'):
                os.fchmod(stream.fileno(), mode)
            else:  # Windows fixture only; production platform check requires Linux.
                os.chmod(temp, mode)
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        self.checkpoint('flushed:' + path.name)
        os.replace(str(temp), str(path))
        self.ops.sync_directory(path.parent)
        self.checkpoint('write:' + path.name)

    def _read(self, path, limit=8192, mode=0o600):
        self.ops.check_parents(path)
        self.ops.check_path(path, mode=mode)
        require(path.stat().st_size <= limit, 'oversized_file')
        return path.read_bytes()

    def _json(self, name, missing=None):
        path = self.root / name
        return parse_json(self._read(path)) if os.path.lexists(path) else missing

    def _owner(self, create=False):
        self.ops.check_platform()
        self.ops.check_parents(self.root)
        self.ops.check_parents(self.unit)
        expected = {'schema': 1, 'installation': self.installation, 'unit_sha256': digest(UNIT), 'launcher_sha256': digest(LAUNCHER)}
        if not os.path.lexists(self.root):
            if not create:
                return False
            require(not os.path.lexists(self.unit) and not self.ops.service_state()['exists'], 'foreign_service')
            self.root.mkdir(mode=0o700)
            self.ops.sync_directory(self.root.parent)
            self._atomic(self.root / 'owner.json', canonical(expected))
        self.ops.check_path(self.root, directory=True, mode=0o700)
        owner = self._json('owner.json')
        require(isinstance(owner, dict) and type(owner.get('schema')) is int and owner == expected, 'foreign_installation')
        return True

    def _owned_unit(self):
        if os.path.lexists(self.unit):
            require(self._read(self.unit, mode=0o644) == UNIT, 'foreign_service')
        state = self.ops.service_state()
        require(not state['exists'] or os.path.lexists(self.unit), 'foreign_service')
        launcher = self.root / 'launcher.py'
        if os.path.lexists(launcher):
            require(self._read(launcher, mode=0o700) == LAUNCHER, 'changed_launcher')
        return state

    def _current(self):
        path = self.root / 'current.json'
        if not os.path.lexists(path):
            return None
        value = parse_json(self._read(path))
        require(value is not None, 'invalid_pointer')
        return validate_pointer(value)

    def _generation(self, generation):
        require(hex_id(generation))
        path = self.root / 'generations' / generation
        self.ops.check_path(path, directory=True, mode=0o700)
        manifest = parse_json(self._read(path / 'manifest.json'))
        require(isinstance(manifest, dict) and set(manifest) == {'schema', 'installation', 'generation', 'architecture', 'binary_sha256', 'config_sha256'}, 'invalid_manifest')
        require(type(manifest['schema']) is int and manifest['schema'] == 1 and manifest['installation'] == self.installation and manifest['generation'] == generation, 'foreign_generation')
        require(manifest['architecture'] in ('x86_64', 'aarch64'), 'invalid_manifest')
        for name, field, limit, mode in [('sing-box', 'binary_sha256', MAX_BINARY, 0o700), ('config.json', 'config_sha256', MAX_CONFIG, 0o600)]:
            require(digest(self._read(path / name, limit, mode)) == manifest[field], 'changed_generation')
        return path

    def _journal(self, journal, phase):
        updated = dict(journal, phase=phase)
        require(len(canonical(updated)) <= 8 * 1024 * 1024, 'journal_too_large')
        self._atomic(self.root / 'journal.json', canonical(updated))
        journal['phase'] = phase
        self.checkpoint(phase)

    def _load_journal(self, path=None):
        path = self.root / 'journal.json' if path is None else path
        if not os.path.lexists(path):
            return None
        journal = parse_json(self._read(path, 8 * 1024 * 1024))
        keys = {'schema', 'installation', 'operation', 'request_sha256', 'before', 'after', 'service', 'created_generation', 'phase'}
        require(isinstance(journal, dict) and type(journal.get('schema')) is int and journal['schema'] in (1, 2) and set(journal) == (keys | {'removed_before'} if journal['schema'] == 2 else keys), 'invalid_journal')
        require(journal['installation'] == self.installation and hex_id(journal['operation']), 'invalid_journal')
        if journal['schema'] == 2:
            removed = self._validate_removal_record(journal['removed_before'])
            require(removed['phase'] == 'removed' and journal['before'] is None and journal['created_generation'] == journal['operation'] and journal['operation'] != removed['operation'], 'invalid_journal')
        require(isinstance(journal['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', journal['request_sha256']), 'invalid_journal')
        validate_pointer(journal['before']); validate_pointer(journal['after'])
        require(journal['after'] is not None and journal['after']['operation'] == journal['operation'] and journal['after']['request_sha256'] == journal['request_sha256'], 'invalid_journal')
        before_revision = journal['before']['revision'] if journal['before'] else journal['removed_before']['revision'] if journal['schema'] == 2 else 0
        require(journal['after']['revision'] == before_revision + 1, 'invalid_journal')
        require(isinstance(journal['service'], dict) and set(journal['service']) == {'exists', 'active', 'enabled'} and all(type(v) is bool for v in journal['service'].values()), 'invalid_journal')
        require(journal['service']['exists'] == (journal['before'] is not None) and (journal['service']['exists'] or not journal['service']['active'] and not journal['service']['enabled']), 'invalid_journal')
        require(journal['after']['previous'] == (journal['before']['generation'] if journal['before'] else None), 'invalid_journal')
        require(journal['created_generation'] is None or journal['created_generation'] == journal['operation'] == journal['after']['generation'], 'invalid_journal')
        if journal['created_generation'] is None:
            require(journal['before'] and journal['before']['previous'] and journal['after']['generation'] == journal['before']['previous'], 'invalid_journal')
        require(journal['phase'] in ('prepared', 'staged', 'unit-installed', 'switched', 'activated', 'rollback-started', 'pointer-restored', 'service-restored', *TERMINAL), 'invalid_journal')
        return journal

    def _finalize_reinstallation(self, journal):
        if journal['schema'] != 2 or journal['phase'] != 'committed':
            return
        removed = journal['removed_before']
        expected = self._removal_receipt(removed)
        path = self.root / 'receipts' / (removed['operation'] + '.json')
        if os.path.lexists(path):
            existing = parse_json(self._read(path))
            require(existing == expected and type(existing.get('schema')) is int and type(existing.get('revision')) is int, 'operation_id_reused')
        else:
            self._atomic(path, canonical(expected))
        marker = self.root / 'removal.json'
        if os.path.lexists(marker):
            require(self._load_removal() == removed, 'removal_operation_mismatch')
            marker.unlink(); self.ops.sync_directory(self.root)
        self.checkpoint('reinstall-removal-archived')

    def _receipt(self, journal):
        folder = self.root / 'receipts'
        if not folder.exists():
            folder.mkdir(mode=0o700)
            self.ops.sync_directory(self.root)
        self.ops.check_path(folder, directory=True, mode=0o700)
        self._finalize_reinstallation(journal)
        result = {'schema': 1, 'operation': journal['operation'], 'request_sha256': journal['request_sha256'], 'state': journal['phase'], 'revision': journal['after']['revision'] if journal['phase'] == 'committed' else (journal['before']['revision'] if journal['before'] else journal['removed_before']['revision'] if journal['schema'] == 2 else 0)}
        self._atomic(folder / (journal['operation'] + '.json'), canonical(result))
        return result

    def _cleanup_generation(self, generation):
        if generation is None:
            return
        folder = self.root / 'generations' / generation
        if not os.path.lexists(folder):
            return
        self.ops.check_path(folder, directory=True, mode=0o700)
        allowed = {'sing-box': 0o700, 'config.json': 0o600, 'manifest.json': 0o600}
        for path in folder.iterdir():
            name = path.name
            require(name in allowed or name in {'.' + n + '.new' for n in allowed}, 'unexpected_generation_file')
            self.ops.check_path(path, mode=allowed.get(name, allowed.get(name[1:-4])))
        for path in folder.iterdir():
            path.unlink()
        folder.rmdir()
        self.ops.sync_directory(folder.parent)

    def _cleanup_reinstallation_temporaries(self, journal):
        pointers = (canonical(journal['after']),) + ((canonical(journal['before']),) if journal['before'] is not None else ())
        expected = [(self.root / '.current.json.new', pointers, 0o600)]
        if not journal['service']['exists']:
            expected += [(self.root / '.launcher.py.new', (LAUNCHER,), 0o700),
                         (self.unit.with_name('.' + self.unit.name + '.new'), (UNIT,), 0o644)]
        owned = []
        for path, data, mode in expected:
            if os.path.lexists(path):
                require(self._read(path, mode=mode) in data, 'changed_reinstallation_temporary')
                owned.append(path)
        for path in owned:
            path.unlink(); self.ops.sync_directory(path.parent)
            self.checkpoint('reinstall-temporary-removed:' + path.name)

    def _restore(self, journal):
        if journal['phase'] in TERMINAL:
            return self._receipt(journal)
        current = self._current()
        require(current in (journal['before'], journal['after']), 'current_changed_externally')
        state = self._owned_unit()
        self._cleanup_reinstallation_temporaries(journal)
        if journal['before']:
            self._generation(journal['before']['generation'])
        self._journal(journal, 'rollback-started')
        if state['exists']:
            self.ops.stop()
        if journal['before'] is None:
            path = self.root / 'current.json'
            if path.exists():
                path.unlink(); self.ops.sync_directory(self.root)
        else:
            self._generation(journal['before']['generation'])
            self._atomic(self.root / 'current.json', canonical(journal['before']))
        self._journal(journal, 'pointer-restored')
        if journal['service']['exists']:
            require(self.unit.exists() and (self.root / 'launcher.py').exists(), 'previous_service_missing')
            self.ops.reload()
            self.ops.set_service(journal['service']['active'], journal['service']['enabled'])
        else:
            if self.unit.exists():
                self.ops.set_service(False, False)
                self.unit.unlink(); self.ops.sync_directory(self.unit.parent)
            self.ops.reload()
            require(not self.ops.service_state()['exists'], 'service_readback_failed')
        self._journal(journal, 'service-restored')
        self._cleanup_generation(journal['created_generation'])
        if journal['schema'] == 2:
            require(self._load_removal() == journal['removed_before'], 'removal_operation_mismatch')
            launcher = self.root / 'launcher.py'
            if os.path.lexists(launcher):
                require(self._read(launcher, mode=0o700) == LAUNCHER, 'changed_launcher')
                launcher.unlink(); self.ops.sync_directory(self.root)
            generations = self.root / 'generations'
            if os.path.lexists(generations):
                self.ops.check_path(generations, directory=True, mode=0o700)
                require(not any(generations.iterdir()), 'unexpected_generation_file')
                generations.rmdir(); self.ops.sync_directory(self.root)
        self._journal(journal, 'rolled-back')
        return self._receipt(journal)

    def _check_removed_baseline(self, removed, journal, prepared_temporary=False):
        require(removed['phase'] == 'removed' and os.path.lexists(self.root / 'removal.json') and not os.path.lexists(self.root / '.removal.json.new'), 'removal_recovery_required')
        require(journal is None or journal['schema'] == 2 and journal['phase'] == 'rolled-back' and journal['removed_before'] == removed, 'recovery_required')
        require(self._current() is None and not os.path.lexists(self.unit) and not self.ops.service_state()['exists'], 'service_state_drift')
        require({path.name for path in self.root.iterdir()} <= {'owner.json', 'operation.lock', 'removal.json', 'journal.json', 'receipts'} | ({'.journal.json.new'} if prepared_temporary else set()), 'unexpected_installation_file')
        for path in self.root.iterdir():
            if path.name == 'receipts':
                self.ops.check_path(path, directory=True, mode=0o700)
                for receipt_path in path.iterdir():
                    require(receipt_path.suffix == '.json' and hex_id(receipt_path.stem), 'unexpected_receipt_file')
                    receipt = parse_json(self._read(receipt_path))
                    require(isinstance(receipt, dict) and set(receipt) == {'schema', 'operation', 'request_sha256', 'state', 'revision'} and type(receipt['schema']) is int and receipt['schema'] == 1 and receipt['operation'] == receipt_path.stem and isinstance(receipt['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', receipt['request_sha256']) and receipt['state'] in (*TERMINAL, 'not-sent', 'removed') and type(receipt['revision']) is int and 0 <= receipt['revision'] <= 2147483647, 'invalid_receipt')
            else:
                self.ops.check_path(path, mode=0o600)

    def inspect(self):
        # Read-only: no mkdir, writable lock, journal/receipt repair or service action.
        result = {'schema': 1, 'kind': 'status', 'installation': self.installation,
                  'state': 'absent', 'revision': 0, 'generation': '', 'previous': '',
                  'operation': '', 'request_sha256': '', 'architecture': '',
                  'binary_sha256': '', 'config_sha256': '', 'journal': 'none',
                  'pending_operation': '', 'pending_request_sha256': '',
                  'previous_available': False, 'service_exists': False,
                  'active': False, 'enabled': False}
        if not self._owner():
            require(not os.path.lexists(self.unit) and not self.ops.service_state()['exists'], 'foreign_service')
            return result
        with self.ops.read_lock(self.root / 'operation.lock'):
            removal = self._load_removal()
            journal = self._load_journal()
            temporary = self.root / '.journal.json.new'
            if os.path.lexists(temporary):
                pending = self._load_journal(temporary)
                if journal is None or journal['phase'] in TERMINAL and journal['operation'] != pending['operation']:
                    require(pending['phase'] == 'prepared' and self._current() == pending['before'] and self._owned_unit() == pending['service'], 'operation_recovery_mismatch')
                    if pending['before']:
                        self._generation(pending['before']['generation'])
                    journal = pending  # Observation only: never promote during status.
                else:
                    require({k:v for k,v in journal.items() if k != 'phase'} == {k:v for k,v in pending.items() if k != 'phase'}, 'operation_recovery_mismatch')
            if removal is not None and (journal is None or journal.get('schema') != 2 or journal['phase'] == 'rolled-back'):
                self._check_removed_baseline(removal, journal)
                result.update({'state': 'removed', 'revision': removal['revision'],
                               'operation': removal['operation'], 'request_sha256': removal['request_sha256'], 'journal': 'removed'})
                return result
            if removal is not None:
                require(journal['removed_before'] == removal, 'removal_operation_mismatch')
            current = self._current()
            service = self._owned_unit()
            pending = journal is not None and journal['phase'] not in TERMINAL
            if journal:
                if pending:
                    require(current in (journal['before'], journal['after']), 'current_changed_externally')
                else:
                    require(current == (journal['after'] if journal['phase'] == 'committed' else journal['before']), 'current_changed_externally')
                result['journal'] = journal['phase']
            result.update({'state': 'pending' if pending else 'reserved',
                           'service_exists': service['exists'], 'active': service['active'],
                           'enabled': service['enabled']})
            if pending:
                result.update({'pending_operation': journal['operation'], 'pending_request_sha256': journal['request_sha256']})
            if current:
                require(journal is not None, 'journal_missing')
                require(service['exists'] and (self.root / 'launcher.py').exists(), 'service_state_drift')
                folder = self._generation(current['generation'])
                manifest = parse_json(self._read(folder / 'manifest.json'))
                require(manifest['architecture'] in ('x86_64', 'aarch64'), 'invalid_manifest')
                result.update({'state': 'pending' if pending else 'installed',
                               'revision': current['revision'], 'generation': current['generation'],
                               'previous': current['previous'] or '', 'operation': current['operation'],
                               'request_sha256': current['request_sha256'], 'architecture': manifest['architecture'],
                               'binary_sha256': manifest['binary_sha256'], 'config_sha256': manifest['config_sha256']})
                if current['previous'] and not pending:
                    try:
                        self._generation(current['previous'])
                        result['previous_available'] = True
                    except (OSError, VpsError):
                        result['previous_available'] = False
            elif not pending:
                require(not service['exists'], 'service_state_drift')
            return result

    def plan_removal(self, expected_revision):
        """Read-only ownership inventory; not permission to delete later by pathname.

        The eventual writer must repeat this under its exclusive operation lock,
        journal intent durably, and verify object identity before every mutation.
        """
        require(type(expected_revision) is int and 1 <= expected_revision <= 2147483647)
        require(self._owner(), 'not_installed')
        with self.ops.read_lock(self.root / 'operation.lock'):
            return self._plan_removal_locked(expected_revision)

    def _plan_removal_locked(self, expected_revision):
        journal = self._load_journal()
        require(journal is not None and journal['phase'] in TERMINAL, 'recovery_required')
        current = self._current()
        require(current is not None and current['revision'] == expected_revision, 'stale_revision')
        require(current == (journal['after'] if journal['phase'] == 'committed' else journal['before']), 'current_changed_externally')
        service = self._owned_unit()
        require(service['exists'] and (self.root / 'launcher.py').exists(), 'service_state_drift')
        files = []
        directories = []
        def capture_directory(path):
            self.ops.check_path(path, directory=True, mode=0o700)
            info = path.lstat()
            directories.append({'path': path.relative_to(self.root).as_posix(), 'device': info.st_dev, 'inode': info.st_ino})
        def capture(path, mode, limit=8192):
            data = self._read(path, limit, mode)
            info = path.lstat()
            files.append({'path': path.relative_to(self.root).as_posix() if path != self.unit else '@unit',
                          'sha256': digest(data), 'size': len(data), 'device': info.st_dev,
                          'inode': info.st_ino, 'mode': mode})
        allowed = {'owner.json', 'current.json', 'journal.json', 'launcher.py',
                   'operation.lock', 'generations', 'receipts'}
        require(all(path.name in allowed for path in self.root.iterdir()), 'unexpected_installation_file')
        for name, mode in [('owner.json', 0o600), ('current.json', 0o600),
                           ('journal.json', 0o600), ('launcher.py', 0o700)]:
            capture(self.root / name, mode, 8 * 1024 * 1024 if name == 'journal.json' else 8192)
        lock = self.root / 'operation.lock'
        if os.path.lexists(lock):
            capture(lock, 0o600)
        generations = self.root / 'generations'
        self.ops.check_path(generations, directory=True, mode=0o700)
        capture_directory(generations)
        names = set()
        for folder in sorted(generations.iterdir()):
            require(hex_id(folder.name), 'unexpected_generation_file')
            self._generation(folder.name)
            capture_directory(folder)
            require({child.name for child in folder.iterdir()} == {'sing-box', 'config.json', 'manifest.json'}, 'unexpected_generation_file')
            names.add(folder.name)
            for name, mode, limit in [('sing-box', 0o700, MAX_BINARY),
                                       ('config.json', 0o600, MAX_CONFIG), ('manifest.json', 0o600, 8192)]:
                capture(folder / name, mode, limit)
        require(current['generation'] in names and (current['previous'] is None or current['previous'] in names), 'changed_generation')
        receipts = self.root / 'receipts'
        self.ops.check_path(receipts, directory=True, mode=0o700)
        capture_directory(receipts)
        for path in sorted(receipts.iterdir()):
            require(path.suffix == '.json' and hex_id(path.stem), 'unexpected_receipt_file')
            receipt = parse_json(self._read(path))
            require(isinstance(receipt, dict) and set(receipt) == {'schema', 'operation', 'request_sha256', 'state', 'revision'} and
                    type(receipt['schema']) is int and receipt['schema'] == 1 and receipt['operation'] == path.stem and
                    isinstance(receipt['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', receipt['request_sha256']) and
                    receipt['state'] in (*TERMINAL, 'not-sent', 'removed') and type(receipt['revision']) is int and
                    0 <= receipt['revision'] <= 2147483647, 'invalid_receipt')
            capture(path, 0o600)
        capture(self.unit, 0o644)
        return {'installation': self.installation, 'revision': current['revision'],
                'service': dict(service), 'files': files, 'generations': sorted(names), 'directories': directories}

    def _load_removal(self):
        path = self.root / 'removal.json'
        if not os.path.lexists(path):
            path = self.root / '.removal.json.new'
        if not os.path.lexists(path):
            return None
        return self._validate_removal_record(parse_json(self._read(path, 4 * 1024 * 1024)))

    def _validate_removal_record(self, record):
        require(isinstance(record, dict) and set(record) == {'schema', 'installation', 'operation', 'request_sha256', 'revision', 'phase', 'inventory'}, 'invalid_removal')
        require(type(record['schema']) is int and record['schema'] == 1 and record['installation'] == self.installation and hex_id(record['operation']), 'invalid_removal')
        require(isinstance(record['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', record['request_sha256']) and type(record['revision']) is int and 2 <= record['revision'] <= 2147483647 and record['phase'] in ('removing', 'removed'), 'invalid_removal')
        plan = record['inventory']
        require(isinstance(plan, dict) and set(plan) == {'installation', 'revision', 'service', 'files', 'generations', 'directories'} and plan['installation'] == self.installation and type(plan['revision']) is int and plan['revision'] == record['revision'] - 1, 'invalid_removal')
        require(isinstance(plan['files'], list) and isinstance(plan['directories'], list) and isinstance(plan['generations'], list) and all(hex_id(g) for g in plan['generations']), 'invalid_removal')
        names = set()
        for item in plan['files']:
            require(isinstance(item, dict) and set(item) == {'path', 'sha256', 'size', 'device', 'inode', 'mode'}, 'invalid_removal')
            name = item['path']
            require(isinstance(name, str) and re.fullmatch(r'(?:@unit|owner[.]json|current[.]json|journal[.]json|launcher[.]py|operation[.]lock|generations/[0-9a-f]{32}/(?:sing-box|config[.]json|manifest[.]json)|receipts/[0-9a-f]{32}[.]json)', name) and name not in names, 'invalid_removal')
            names.add(name)
            mode = 0o644 if name == '@unit' else 0o700 if name == 'launcher.py' or name.endswith('/sing-box') else 0o600
            require(type(item['mode']) is int and item['mode'] == mode and isinstance(item['sha256'], str) and re.fullmatch('[0-9a-f]{64}', item['sha256']), 'invalid_removal')
            require(all(type(item[k]) is int and item[k] >= 0 for k in ('size', 'device', 'inode')) and item['size'] <= MAX_BINARY, 'invalid_removal')
        require({'@unit', 'owner.json', 'current.json', 'journal.json', 'launcher.py'} <= names, 'invalid_removal')
        directories = set()
        for item in plan['directories']:
            require(isinstance(item, dict) and set(item) == {'path', 'device', 'inode'} and isinstance(item['path'], str) and re.fullmatch(r'(?:generations|receipts|generations/[0-9a-f]{32})', item['path']) and item['path'] not in directories and all(type(item[k]) is int and item[k] >= 0 for k in ('device', 'inode')), 'invalid_removal')
            directories.add(item['path'])
        require(directories == {'generations', 'receipts'} | {'generations/' + g for g in plan['generations']}, 'invalid_removal')
        require(all({'generations/' + g + '/' + n for n in ('sing-box', 'config.json', 'manifest.json')} <= names for g in plan['generations']), 'invalid_removal')
        return record

    def _removal_receipt(self, record):
        return {'schema': 1, 'operation': record['operation'], 'request_sha256': record['request_sha256'], 'state': record['phase'], 'revision': record['revision']}

    def _verify_removal_file(self, item):
        path = self.unit if item['path'] == '@unit' else self.root / item['path']
        if not os.path.lexists(path):
            return None
        data = self._read(path, MAX_BINARY, item['mode'])
        info = path.lstat()
        require((info.st_dev, info.st_ino, len(data), digest(data)) == (item['device'], item['inode'], item['size'], item['sha256']), 'removal_object_changed')
        return path

    def _resume_removal_locked(self, record):
        plan = record['inventory']
        temporary = self.root / '.removal.json.new'
        if os.path.lexists(temporary):
            pending = parse_json(self._read(temporary, 4 * 1024 * 1024))
            require(pending in (record, dict(record, phase='removed')), 'invalid_removal')
            temporary.unlink(); self.ops.sync_directory(self.root)
        expected_files = {item['path'] for item in plan['files']} - {'@unit'}
        expected_dirs = {item['path'] for item in plan['directories']}
        # Whole-tree refusal before any new effect; never recursively delete.
        for folder, dirs, files in os.walk(self.root, followlinks=False):
            for name in dirs:
                path = Path(folder) / name
                require(path.relative_to(self.root).as_posix() in expected_dirs, 'unexpected_installation_file')
                self.ops.check_path(path, directory=True, mode=0o700)
            for name in files:
                require((Path(folder) / name).relative_to(self.root).as_posix() in expected_files | {'removal.json'}, 'unexpected_installation_file')
        for item in plan['directories']:
            path = self.root / item['path']
            if os.path.lexists(path):
                self.ops.check_path(path, directory=True, mode=0o700)
                info = path.lstat()
                require((info.st_dev, info.st_ino) == (item['device'], item['inode']), 'removal_object_changed')
        for item in plan['files']:
            self._verify_removal_file(item)
        unit = next(item for item in plan['files'] if item['path'] == '@unit')
        if os.path.lexists(self.unit):
            self.ops.set_service(False, False)
            state = self.ops.service_state()
            require(not state['active'] and not state['enabled'], 'service_readback_failed')
            self.checkpoint('removal-service-disabled')
            self._verify_removal_file(unit).unlink()
            self.ops.sync_directory(self.unit.parent)
            self.checkpoint('removal-unit-deleted')
        self.ops.reload()
        require(not self.ops.service_state()['exists'], 'service_readback_failed')
        for item in plan['files']:
            if item['path'] in ('@unit', 'owner.json', 'operation.lock'):
                continue
            path = self._verify_removal_file(item)
            if path is not None:
                path.unlink(); self.ops.sync_directory(path.parent)
                self.checkpoint('removal-file:' + item['path'])
        for item in sorted(plan['directories'], key=lambda value: value['path'].count('/'), reverse=True):
            path = self.root / item['path']
            if os.path.lexists(path):
                self.ops.check_path(path, directory=True, mode=0o700)
                info = path.lstat()
                require((info.st_dev, info.st_ino) == (item['device'], item['inode']), 'removal_object_changed')
                path.rmdir(); self.ops.sync_directory(path.parent)
                self.checkpoint('removal-directory:' + item['path'])
        if record['phase'] != 'removed':
            record = dict(record, phase='removed')
            self._atomic(self.root / 'removal.json', canonical(record))
            self.checkpoint('removal-committed')
        return self._removal_receipt(record)

    def _removal_fence(self, operation, request_hash, revision):
        path = self.root / 'receipts' / (operation + '.json')
        temporary = path.with_name('.' + path.name + '.new')
        source = path if os.path.lexists(path) else temporary
        if not os.path.lexists(source):
            return None
        receipt = parse_json(self._read(source))
        state = receipt.get('state') if isinstance(receipt, dict) else None
        require(state in ('not-sent', 'removed'), 'operation_id_reused')
        expected = {'schema': 1, 'operation': operation, 'request_sha256': request_hash,
                    'state': state, 'revision': revision + (1 if state == 'removed' else 0)}
        require(receipt == expected and type(receipt.get('schema')) is int and type(receipt.get('revision')) is int, 'operation_id_reused')
        if source == temporary:
            self._atomic(path, canonical(expected))
        return receipt

    def remove(self, request):
        require(isinstance(request, dict) and set(request) == {'schema', 'installation', 'operation', 'expected_revision', 'action'} and type(request['schema']) is int and request['schema'] == 1 and request['installation'] == self.installation and request['action'] == 'remove' and hex_id(request['operation']) and type(request['expected_revision']) is int and 1 <= request['expected_revision'] < 2147483647)
        require(self._owner(), 'not_installed')
        with self.ops.lock(self.root / 'operation.lock'):
            request_hash = digest(canonical(request))
            fence = self._removal_fence(request['operation'], request_hash, request['expected_revision'])
            if fence is not None:
                return fence
            record = self._load_removal()
            if record is not None:
                require(record['operation'] == request['operation'] and record['request_sha256'] == request_hash, 'removal_already_started')
            else:
                plan = self._plan_removal_locked(request['expected_revision'])
                require(request['operation'] not in {path['path'][9:-5] for path in plan['files'] if path['path'].startswith('receipts/')}, 'operation_id_reused')
                record = {'schema': 1, 'installation': self.installation, 'operation': request['operation'], 'request_sha256': request_hash, 'revision': request['expected_revision'] + 1, 'phase': 'removing', 'inventory': plan}
                require(len(canonical(record)) <= 4 * 1024 * 1024, 'removal_inventory_too_large')
                self._atomic(self.root / 'removal.json', canonical(record))
                self.checkpoint('removal-prepared')
            if not os.path.lexists(self.root / 'removal.json'):
                self._atomic(self.root / 'removal.json', canonical(record))
            return self._removal_receipt(record) if record['phase'] == 'removed' else self._resume_removal_locked(record)

    def recover_removal(self, request=None):
        if request is not None:
            require(isinstance(request, dict) and set(request) == {'schema', 'installation', 'action', 'operation', 'expected_revision', 'original_request_sha256'} and type(request['schema']) is int and request['schema'] == 1 and request['installation'] == self.installation and request['action'] == 'recover-remove' and hex_id(request['operation']) and type(request['expected_revision']) is int and 1 <= request['expected_revision'] < 2147483647)
            original = {key: request[key] for key in ('schema', 'installation', 'operation', 'expected_revision')}
            original['action'] = 'remove'
            require(request['original_request_sha256'] == digest(canonical(original)), 'request_hash_mismatch')
        require(self._owner(), 'not_installed')
        with self.ops.lock(self.root / 'operation.lock'):
            if request is not None:
                fence = self._removal_fence(request['operation'], request['original_request_sha256'], request['expected_revision'])
                if fence is not None:
                    return fence
            record = self._load_removal()
            if record is None:
                require(request is not None, 'removal_missing')
                # The exclusive server lock orders this durable refusal before a
                # delayed copy of the removal request. No service action occurs.
                self._plan_removal_locked(request['expected_revision'])
                receipt = {'schema': 1, 'operation': request['operation'],
                           'request_sha256': request['original_request_sha256'],
                           'state': 'not-sent', 'revision': request['expected_revision']}
                self._atomic(self.root / 'receipts' / (request['operation'] + '.json'), canonical(receipt))
                self.checkpoint('removal-fenced')
                return receipt
            if request is not None:
                require((record['operation'], record['request_sha256'], record['revision']) ==
                        (request['operation'], request['original_request_sha256'], request['expected_revision'] + 1), 'removal_operation_mismatch')
            if not os.path.lexists(self.root / 'removal.json'):
                self._atomic(self.root / 'removal.json', canonical(record))
            return self._removal_receipt(record) if record['phase'] == 'removed' else self._resume_removal_locked(record)

    def _reinstallation_receipt(self, operation, request_hash, revision):
        path = self.root / 'receipts' / (operation + '.json')
        temporary = path.with_name('.' + path.name + '.new')
        source = path if os.path.lexists(path) else temporary
        if not os.path.lexists(source):
            return None
        receipt = parse_json(self._read(source))
        state = receipt.get('state') if isinstance(receipt, dict) else None
        require(state in TERMINAL, 'operation_id_reused')
        expected = {'schema': 1, 'operation': operation, 'request_sha256': request_hash,
                    'state': state, 'revision': revision + (1 if state == 'committed' else 0)}
        require(receipt == expected and type(receipt.get('schema')) is int and type(receipt.get('revision')) is int, 'operation_id_reused')
        if source == temporary:
            os.replace(str(temporary), str(path)); self.ops.sync_directory(path.parent)
            self.checkpoint('reinstall-receipt-promoted')
        return receipt

    def recover_reinstallation(self, request):
        require(isinstance(request, dict) and set(request) == {'schema', 'installation', 'action', 'operation', 'expected_revision', 'original_request_sha256'} and type(request['schema']) is int and request['schema'] == 1 and request['installation'] == self.installation and request['action'] == 'recover-reinstall' and hex_id(request['operation']) and type(request['expected_revision']) is int and 1 <= request['expected_revision'] < 2147483647 and isinstance(request['original_request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', request['original_request_sha256']))
        require(self._owner(), 'not_installed')
        with self.ops.lock(self.root / 'operation.lock'):
            cached = self._reinstallation_receipt(request['operation'], request['original_request_sha256'], request['expected_revision'])
            if cached is not None:
                return cached
            journal = self._load_journal()
            temporary = self.root / '.journal.json.new'
            expected = (request['operation'], request['original_request_sha256'], request['expected_revision'])
            if os.path.lexists(temporary):
                pending = self._load_journal(temporary)
                require(pending is not None and pending['schema'] == 2 and
                        (pending['operation'], pending['request_sha256'], pending['removed_before']['revision']) == expected,
                        'reinstallation_operation_mismatch')
                if journal is None or journal['schema'] == 2 and journal['phase'] == 'rolled-back' and journal['operation'] != pending['operation']:
                    require(pending['phase'] == 'prepared' and self._load_removal() == pending['removed_before'], 'invalid_journal')
                    self._check_removed_baseline(pending['removed_before'], journal, prepared_temporary=True)
                    # Promote the already-flushed intent; never erase it and then
                    # create another temporary copy across a crash window.
                    os.replace(str(temporary), str(self.root / 'journal.json'))
                    self.ops.sync_directory(self.root)
                    journal = pending
                    self.checkpoint('reinstall-first-journal-promoted')
                else:
                    require(journal['schema'] == 2 and journal['operation'] == pending['operation'] and journal['request_sha256'] == pending['request_sha256'] and journal['removed_before'] == pending['removed_before'], 'reinstallation_operation_mismatch')
            if journal is None or journal['schema'] == 2 and journal['phase'] == 'rolled-back' and journal['operation'] != request['operation']:
                removed = self._load_removal()
                require(removed is not None, 'removed_baseline_required')
                self._check_removed_baseline(removed, journal)
                require(removed['revision'] == request['expected_revision'], 'stale_revision')
                retired = set(removed['inventory']['generations']) | {item['path'][9:-5] for item in removed['inventory']['files'] if item['path'].startswith('receipts/')} | {removed['operation']}
                require(request['operation'] not in retired, 'operation_id_reused')
                folder = self.root / 'receipts'
                if not os.path.lexists(folder):
                    folder.mkdir(mode=0o700); self.ops.sync_directory(self.root)
                self.ops.check_path(folder, directory=True, mode=0o700)
                receipt = {'schema': 1, 'operation': request['operation'], 'request_sha256': request['original_request_sha256'], 'state': 'rolled-back', 'revision': request['expected_revision']}
                self._atomic(folder / (request['operation'] + '.json'), canonical(receipt))
                self.checkpoint('reinstall-missing-intent-fenced')
                return receipt
            require(journal is not None and journal['schema'] == 2, 'reinstallation_missing')
            require((journal['operation'], journal['request_sha256'], journal['removed_before']['revision']) == expected, 'reinstallation_operation_mismatch')
            return self._restore(journal)

    def _fence_missing_operation(self, request, journal):
        require(journal is None or journal['phase'] in TERMINAL, 'operation_recovery_mismatch')
        current = self._current()
        if journal is not None:
            require(current == (journal['after'] if journal['phase'] == 'committed' else journal['before']), 'current_changed_externally')
        else:
            require(current is None, 'journal_missing')
        require((current['revision'] if current else 0) == request['expected_revision'], 'stale_revision')
        state = self._owned_unit()
        require((current is not None) == state['exists'], 'service_state_drift')
        if current is not None:
            require((self.root / 'launcher.py').exists(), 'service_state_drift')
            self._generation(current['generation'])
        require(not os.path.lexists(self.root / 'generations' / request['operation']), 'operation_id_reused')
        allowed = {'owner.json', 'operation.lock', 'journal.json', 'current.json', 'launcher.py', 'generations', 'receipts'}
        require({path.name for path in self.root.iterdir()} <= allowed, 'unexpected_installation_file')
        for path in self.root.iterdir():
            if path.name in ('generations', 'receipts'):
                self.ops.check_path(path, directory=True, mode=0o700)
                if path.name == 'generations' and current is None:
                    require(not any(path.iterdir()), 'unexpected_generation_file')
                if path.name == 'receipts':
                    for item in path.iterdir():
                        require(item.suffix == '.json' and hex_id(item.stem), 'unexpected_receipt_file')
                        self.ops.check_path(item, mode=0o600)
                        value = parse_json(self._read(item))
                        require(isinstance(value, dict) and set(value) == {'schema', 'operation', 'request_sha256', 'state', 'revision'} and type(value['schema']) is int and value['schema'] == 1 and value['operation'] == item.stem and isinstance(value['request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', value['request_sha256']) and value['state'] in (*TERMINAL, 'not-sent', 'removed') and type(value['revision']) is int and 0 <= value['revision'] <= 2147483647, 'invalid_receipt')
            else:
                self.ops.check_path(path, mode=0o700 if path.name == 'launcher.py' else 0o600)
        folder = self.root / 'receipts'
        if not os.path.lexists(folder):
            folder.mkdir(mode=0o700); self.ops.sync_directory(self.root)
        receipt = {'schema': 1, 'operation': request['operation'], 'request_sha256': request['original_request_sha256'], 'state': 'rolled-back', 'revision': request['expected_revision']}
        self._atomic(folder / (request['operation'] + '.json'), canonical(receipt))
        self.checkpoint('operation-missing-intent-fenced')
        return receipt

    def recover_operation(self, request):
        require(isinstance(request, dict) and set(request) == {'schema', 'installation', 'action', 'operation', 'expected_revision', 'original_request_sha256'} and type(request['schema']) is int and request['schema'] == 1 and request['installation'] == self.installation and request['action'] == 'recover-operation' and hex_id(request['operation']) and type(request['expected_revision']) is int and 0 <= request['expected_revision'] < 2147483647 and isinstance(request['original_request_sha256'], str) and re.fullmatch('[0-9a-f]{64}', request['original_request_sha256']))
        require(self._owner(create=request['expected_revision'] == 0), 'not_installed')
        with self.ops.lock(self.root / 'operation.lock'):
            cached = self._reinstallation_receipt(request['operation'], request['original_request_sha256'], request['expected_revision'])
            if cached is not None:
                return cached
            require(not any(os.path.lexists(self.root / name) for name in ('removal.json', '.removal.json.new')), 'removal_recovery_required')
            journal = self._load_journal()
            expected = (request['operation'], request['original_request_sha256'], request['expected_revision'])
            def identity(value):
                require(value is not None and value['schema'] == 1, 'operation_recovery_mismatch')
                return (value['operation'], value['request_sha256'], value['before']['revision'] if value['before'] else 0)
            temporary = self.root / '.journal.json.new'
            if os.path.lexists(temporary):
                pending = self._load_journal(temporary)
                require(identity(pending) == expected, 'operation_recovery_mismatch')
                if journal is None or journal['phase'] in TERMINAL and journal['operation'] != pending['operation']:
                    require(pending['phase'] == 'prepared' and self._current() == pending['before'] and self._owned_unit() == pending['service'], 'operation_recovery_mismatch')
                    if pending['before']:
                        self._generation(pending['before']['generation'])
                    os.replace(str(temporary), str(self.root / 'journal.json')); self.ops.sync_directory(self.root)
                    journal = pending; self.checkpoint('operation-first-journal-promoted')
                else:
                    require(identity(journal) == expected and {k:v for k,v in journal.items() if k != 'phase'} == {k:v for k,v in pending.items() if k != 'phase'}, 'operation_recovery_mismatch')
            if journal is None or journal['phase'] in TERMINAL and journal['operation'] != request['operation']:
                return self._fence_missing_operation(request, journal)
            require(identity(journal) == expected, 'operation_recovery_mismatch')
            return self._restore(journal)

    def recover(self):
        # Legacy unbound requests observe completed history only. They cannot
        # roll back whichever newer operation happens to hold the server journal.
        if not self._owner():
            require(not os.path.lexists(self.unit) and not self.ops.service_state()['exists'], 'foreign_service')
            return {'state': 'absent', 'revision': 0}
        with self.ops.read_lock(self.root / 'operation.lock'):
            journal = self._load_journal()
            require(journal is None or journal['schema'] != 2, 'reinstallation_recovery_required')
            require(not any(os.path.lexists(self.root / name) for name in ('removal.json', '.removal.json.new')), 'removal_recovery_required')
            require(not os.path.lexists(self.root / '.journal.json.new') and (journal is None or journal['phase'] in TERMINAL), 'operation_recovery_required')
            if journal is None:
                return {'state': 'idle', 'revision': (self._current() or {}).get('revision', 0)}
            return {'schema': 1, 'operation': journal['operation'], 'request_sha256': journal['request_sha256'], 'state': journal['phase'], 'revision': journal['after']['revision'] if journal['phase'] == 'committed' else journal['before']['revision'] if journal['before'] else 0}

    def apply(self, request):
        require(isinstance(request, dict) and type(request.get('schema')) is int and request.get('schema') == 1 and request.get('installation') == self.installation)
        action = request.get('action')
        common = {'schema', 'installation', 'operation', 'expected_revision', 'action', 'active', 'enabled'}
        deploy = {'binary', 'config', 'binary_sha256', 'config_sha256', 'architecture'}
        require(action in ('deploy', 'reinstall', 'rollback') and set(request) == (common | deploy if action in ('deploy', 'reinstall') else common))
        require(hex_id(request['operation']) and type(request['expected_revision']) is int and 0 <= request['expected_revision'] < 2147483647)
        require(type(request['active']) is bool and type(request['enabled']) is bool)
        binary = config = None
        if action in ('deploy', 'reinstall'):
            binary, config = decode(request['binary'], MAX_BINARY), decode(request['config'], MAX_CONFIG)
            require(digest(binary) == request['binary_sha256'] and digest(config) == request['config_sha256'], 'payload_hash_mismatch')
            require(request['architecture'] in ('x86_64', 'aarch64') and len(binary) >= 64 and binary[:6] == b'\x7fELF\x02\x01' and struct.unpack('<H', binary[16:18])[0] in (2, 3) and struct.unpack('<H', binary[18:20])[0] == {'x86_64': 62, 'aarch64': 183}[request['architecture']], 'unsupported_binary')
            validate_config(config)
        request_hash = digest(canonical(request))
        if not os.path.lexists(self.root):
            require(action == 'deploy' and request['expected_revision'] == 0, 'not_installed')
        require(self._owner(create=action == 'deploy'), 'not_installed')
        with self.ops.lock(self.root / 'operation.lock'):
            cached = self._reinstallation_receipt(request['operation'], request_hash, request['expected_revision'])
            if cached is not None:
                return cached
            require(not os.path.lexists(self.root / '.journal.json.new'), 'reinstallation_recovery_required' if action == 'reinstall' else 'operation_recovery_required')
            previous = self._load_journal()
            if previous and previous['schema'] == 2 and previous['phase'] == 'committed':
                self._receipt(previous)
            removed = self._load_removal()
            require(not os.path.lexists(self.root / '.removal.json.new'), 'removal_recovery_required')
            require(removed is None or action == 'reinstall', 'removal_recovery_required')
            if previous:
                if previous['phase'] not in TERMINAL:
                    raise VpsError('recovery_required')
                self._receipt(previous)
            receipt_path = self.root / 'receipts' / (request['operation'] + '.json')
            if receipt_path.exists():
                receipt = parse_json(self._read(receipt_path))
                require(isinstance(receipt, dict) and set(receipt) == {'schema', 'operation', 'request_sha256', 'state', 'revision'} and type(receipt['schema']) is int and receipt['schema'] == 1 and receipt['operation'] == request['operation'] and receipt['state'] in TERMINAL and type(receipt['revision']) is int and 0 <= receipt['revision'] <= 2147483647, 'invalid_receipt')
                require(receipt['request_sha256'] == request_hash, 'operation_id_reused')
                return receipt
            before = self._current()
            if action == 'reinstall':
                require(removed is not None, 'removed_baseline_required')
                self._check_removed_baseline(removed, previous)
                retired = set(removed['inventory']['generations']) | {item['path'][9:-5] for item in removed['inventory']['files'] if item['path'].startswith('receipts/')} | {removed['operation']}
                require(request['operation'] not in retired, 'operation_id_reused')
            require((before['revision'] if before else removed['revision'] if action == 'reinstall' else 0) == request['expected_revision'], 'stale_revision')
            state = self._owned_unit()
            require((before is not None) == state['exists'], 'service_state_drift')
            if before:
                self._generation(before['generation'])
            if action == 'rollback':
                require(before and before['previous'], 'no_previous_generation')
                self._generation(before['previous'])
            if action in ('deploy', 'reinstall'):
                generations = self.root / 'generations'
                if os.path.lexists(generations):
                    self.ops.check_path(generations, directory=True, mode=0o700)
                require(not os.path.lexists(generations / request['operation']), 'generation_exists')
            after = {'schema': 1, 'revision': request['expected_revision'] + 1, 'generation': request['operation'] if action in ('deploy', 'reinstall') else before['previous'], 'previous': before['generation'] if before else None, 'operation': request['operation'], 'request_sha256': request_hash}
            journal = {'schema': 1, 'installation': self.installation, 'operation': request['operation'], 'request_sha256': request_hash, 'before': before, 'after': after, 'service': state, 'created_generation': request['operation'] if action in ('deploy', 'reinstall') else None, 'phase': 'prepared'}
            if action == 'reinstall':
                journal['schema'] = 2; journal['removed_before'] = removed
            self._journal(journal, 'prepared')
            try:
                if action in ('deploy', 'reinstall'):
                    generations = self.root / 'generations'
                    if not generations.exists():
                        generations.mkdir(mode=0o700); self.ops.sync_directory(self.root)
                    self.ops.check_path(generations, directory=True, mode=0o700)
                    folder = generations / request['operation']
                    require(not os.path.lexists(folder), 'generation_exists')
                    folder.mkdir(mode=0o700); self.ops.sync_directory(generations)
                    self._atomic(folder / 'sing-box', binary, 0o700)
                    self._atomic(folder / 'config.json', config)
                    manifest = {'schema': 1, 'installation': self.installation, 'generation': request['operation'], 'architecture': request['architecture'], 'binary_sha256': digest(binary), 'config_sha256': digest(config)}
                    self._atomic(folder / 'manifest.json', canonical(manifest))
                    self._generation(request['operation'])
                    self.ops.validate_binary(folder)
                self._journal(journal, 'staged')
                if not state['exists']:
                    self._atomic(self.root / 'launcher.py', LAUNCHER, 0o700)
                    require(not os.path.lexists(self.unit), 'foreign_service')
                    self._atomic(self.unit, UNIT, 0o644)
                    self.ops.reload()
                self._journal(journal, 'unit-installed')
                self._atomic(self.root / 'current.json', canonical(after))
                self._journal(journal, 'switched')
                self.ops.set_service(request['active'], request['enabled'])
                self._journal(journal, 'activated')
                self._journal(journal, 'committed')
                return self._receipt(journal)
            except Exception:
                # Disk phase is authoritative, including a failure after atomic commit.
                try:
                    journal = self._load_journal()
                except Exception:
                    raise VpsError('recovery_required')
                if journal and journal['phase'] == 'committed':
                    try:
                        return self._receipt(journal)
                    except Exception:
                        raise VpsError('committed_receipt_pending')
                try:
                    self._restore(journal)
                except Exception:
                    raise VpsError('recovery_required')
                raise VpsError('operation_rolled_back')


def main():
    try:
        data = sys.stdin.buffer.read(MAX_REQUEST + 1)
        require(len(data) <= MAX_REQUEST, 'request_too_large')
        request = parse_json(data)
        require(isinstance(request, dict) and hex_id(request.get('installation')))
        transaction = Transaction(request['installation'])
        if request.get('action') == 'recover-operation':
            result = transaction.recover_operation(request)
        elif request.get('action') == 'recover-reinstall':
            result = transaction.recover_reinstallation(request)
        elif request.get('action') == 'recover-remove' and 'operation' in request:
            result = transaction.recover_removal(request)
        elif request.get('action') in ('recover', 'recover-remove', 'status'):
            require(set(request) == {'schema', 'installation', 'action'} and type(request['schema']) is int and request['schema'] == 1)
            result = transaction.recover_removal() if request['action'] == 'recover-remove' else transaction.recover() if request['action'] == 'recover' else transaction.inspect()
        elif request.get('action') == 'remove':
            result = transaction.remove(request)
        else:
            result = transaction.apply(request)
        sys.stdout.buffer.write(canonical({'schema': 1, 'result': result}) + b'\n')
        return 0
    except VpsError as error:
        sys.stdout.buffer.write(canonical({'schema': 1, 'error': error.code}) + b'\n')
        return 2
    except Exception:
        sys.stdout.buffer.write(b'{"schema":1,"error":"agent_failed"}\n')
        return 3


if __name__ == '__main__':
    sys.exit(main())
