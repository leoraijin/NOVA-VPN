using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NovaVpn;

// Per-directory instance: production uses the personal root; isolated consumers
// and tests supply their own root without changing any global path or entropy.
internal sealed class EncryptedStateFiles
{
    const int MaximumFile=64*1024*1024;
    readonly object sync=new object();
    readonly string directory,statePath,entropyPath,leasePath;
    readonly Func<AppState,byte[]> serialize;
    readonly Func<byte[],AppState> deserialize;
    readonly Action<AppState> normalize;
    readonly Func<AppState> createDefault;
    readonly byte[] legacyEntropy;
    readonly Action<string> checkpoint;
    internal EncryptedStateFiles(string directory,Func<AppState,byte[]> serialize,Func<byte[],AppState> deserialize,Action<AppState> normalize,Func<AppState> createDefault,byte[] legacyEntropy,Action<string> checkpoint)
    {
        if(!Path.IsPathRooted(directory))throw new ArgumentException("Нужен абсолютный каталог настроек.");
        string root=Path.GetPathRoot(directory);if(root=="\\"||root=="/"||root.EndsWith(":"))throw new ArgumentException("Нужен полностью определённый путь каталога настроек.");
        this.directory=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);if(this.directory==Path.GetPathRoot(this.directory).TrimEnd(Path.DirectorySeparatorChar))throw new ArgumentException("Нельзя использовать корень диска для настроек.");
        this.serialize=serialize;this.deserialize=deserialize;this.normalize=normalize;this.createDefault=createDefault;this.legacyEntropy=(byte[])legacyEntropy.Clone();this.checkpoint=checkpoint??(_=>{});
        statePath=Path.Combine(this.directory,"state.dat");entropyPath=Path.Combine(this.directory,"state.entropy");leasePath=Path.Combine(this.directory,".state-write.lock");
    }
    void CheckDirectory(){for(var item=new DirectoryInfo(directory);item!=null;item=item.Parent)if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Каталог настроек не должен быть перенаправлен.");}
    static void CheckFile(string path){try{if((File.GetAttributes(path)&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)throw new IOException("Файл настроек не должен быть каталогом или ссылкой.");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}}
    static byte[] ReadFile(string path,int maximum){CheckFile(path);using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)){if(stream.Length<1||stream.Length>maximum)throw new InvalidDataException("Некорректный размер файла настроек.");var bytes=new byte[(int)stream.Length];int at=0;while(at<bytes.Length){int n=stream.Read(bytes,at,bytes.Length-at);if(n==0)throw new EndOfStreamException();at+=n;}return bytes;}}
    byte[] Entropy(bool create)
    {
        if(File.Exists(entropyPath)){var value=ReadFile(entropyPath,65536);if(value.Length<32)throw new InvalidDataException("Повреждён файл защиты настроек.");return value;}
        CheckFile(entropyPath);if(!create)return null;
        var entropy=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(entropy);
        string temporary=entropyPath+".next-"+Guid.NewGuid().ToString("N");
        try{WriteFlushed(temporary,entropy);checkpoint("entropy-flushed");File.Move(temporary,entropyPath);try{File.SetAttributes(entropyPath,FileAttributes.Hidden);}catch(IOException){}catch(UnauthorizedAccessException){}checkpoint("entropy-published");return entropy;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static void WriteFlushed(string path,byte[] bytes){using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}}
    internal AppState Decode(string encoded)
    {
        CheckDirectory();var cipher=Convert.FromBase64String(encoded);byte[] plain=null;
        try{
            var entropy=Entropy(false);
            if(entropy!=null)try{plain=ProtectedData.Unprotect(cipher,entropy,DataProtectionScope.CurrentUser);}catch(CryptographicException){}
            if(plain==null)plain=ProtectedData.Unprotect(cipher,legacyEntropy,DataProtectionScope.CurrentUser);
            var state=deserialize(plain);if(state==null)throw new InvalidDataException("Пустой файл настроек.");normalize(state);return state;
        }finally{if(plain!=null)Array.Clear(plain,0,plain.Length);}
    }
    AppState TryRead(string path,bool strictIo=false)
    {
        try{var bytes=ReadFile(path,MaximumFile);using(var stream=new MemoryStream(bytes))using(var reader=new StreamReader(stream,Encoding.UTF8,true))return Decode(reader.ReadToEnd());}
        catch(FileNotFoundException){return null;}
        catch(IOException){if(strictIo)throw;return null;}
        catch(UnauthorizedAccessException){if(strictIo)throw;return null;}
        // Preserve startup recovery for malformed legacy object graphs as well as bytes.
        catch(Exception){return null;}
    }
    internal AppState Load(){lock(sync){try{CheckDirectory();return TryRead(statePath)??TryRead(statePath+".bak")??createDefault();}catch(Exception error)when(error is IOException||error is UnauthorizedAccessException){return createDefault();}}}
    internal void Save(AppState state)
    {
        if(state==null)return;
        lock(sync){
            CheckDirectory();
            // Protect new stores; preserve existing directory ACL/ownership until
            // an explicit legacy ownership migration has been accepted.
            if(!Directory.Exists(directory))PrivateRuntimeFiles.PrepareDirectory(directory);
            CheckFile(leasePath);
            // Cross-instance/process exclusion. Readers do not create a lease or
            // entropy, and continue to see the old or new complete file.
            using(var lease=new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){
                CheckFile(statePath);CheckFile(statePath+".bak");CheckFile(entropyPath);
                bool currentValid=TryRead(statePath,true)!=null;
                byte[] plain=serialize(state);byte[] cipher;
                try{if(plain.Length>MaximumFile/2)throw new InvalidDataException("Настройки слишком большие.");cipher=ProtectedData.Protect(plain,Entropy(true),DataProtectionScope.CurrentUser);}finally{Array.Clear(plain,0,plain.Length);}
                var encoded=new UTF8Encoding(false).GetBytes(Convert.ToBase64String(cipher));if(encoded.Length>MaximumFile)throw new InvalidDataException("Настройки слишком большие.");
                string temporary=statePath+".next-"+Guid.NewGuid().ToString("N");
                try{
                    WriteFlushed(temporary,encoded);checkpoint("state-flushed");
                    // A corrupt primary must never displace the retained backup.
                    if(File.Exists(statePath))File.Replace(temporary,statePath,currentValid?statePath+".bak":null);
                    else File.Move(temporary,statePath);
                    checkpoint("state-published");
                }finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
        }
    }
}