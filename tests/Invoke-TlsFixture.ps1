function Invoke-NovaTlsFixture {
param([string]$OutputDirectory,[scriptblock]$Run,[string[]]$AdditionalDnsNames=@())
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Use PowerShell 7 to generate ephemeral fixture keys without a certificate store'}

# All keys exist only in this unique private fixture directory. Nothing is added
# to a Windows certificate store, and no global trust policy is changed.
$work=Join-Path $OutputDirectory ('dual-core-tls-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$owner=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=[Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($owner); $acl.SetAccessRuleProtection($true,$false)
$inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
foreach($sid in @($owner,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
}
Set-Acl -LiteralPath $work -AclObject $acl
function Write-Pem([string]$Path,[string]$Label,[byte[]]$Bytes){
    $body=[Convert]::ToBase64String($Bytes,[Base64FormattingOptions]::InsertLineBreaks)
    [IO.File]::WriteAllText($Path,"-----BEGIN $Label-----`n$body`n-----END $Label-----`n",[Text.UTF8Encoding]::new($false))
}
$caKey=$wrongKey=$leafKey=$ca=$wrong=$leaf=$null
try {
    $caKey=[Security.Cryptography.RSA]::Create(2048)
    $wrongKey=[Security.Cryptography.RSA]::Create(2048)
    $leafKey=[Security.Cryptography.RSA]::Create(2048)
    $sha=[Security.Cryptography.HashAlgorithmName]::SHA256
    $padding=[Security.Cryptography.RSASignaturePadding]::Pkcs1
    $start=[DateTimeOffset]::UtcNow.AddMinutes(-5); $end=[DateTimeOffset]::UtcNow.AddDays(1)
    $caReq=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=NOVA fixture CA',$caKey,$sha,$padding)
    $caReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $caReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true))
    $caReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($caReq.PublicKey,$false))
    $ca=$caReq.CreateSelfSigned($start,$end)
    $wrongReq=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=NOVA unrelated fixture CA',$wrongKey,$sha,$padding)
    $wrongReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $wrong=$wrongReq.CreateSelfSigned($start,$end)
    $leafReq=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost',$leafKey,$sha,$padding)
    $leafReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
    $leafReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]'DigitalSignature,KeyEncipherment',$true))
    $usages=[Security.Cryptography.OidCollection]::new(); [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
    $leafReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages,$false))
    $san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new(); $san.AddDnsName('localhost'); foreach($name in $AdditionalDnsNames){$san.AddDnsName($name)}
    $leafReq.CertificateExtensions.Add($san.Build())
    $serial=[byte[]]::new(16); [Security.Cryptography.RandomNumberGenerator]::Fill($serial); $serial[0]=1
    $leafReq.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509AuthorityKeyIdentifierExtension]::CreateFromCertificate($ca,$true,$false))
    $leaf=$leafReq.Create($ca,$start.AddSeconds(1),$end.AddSeconds(-1),$serial)
    Write-Pem (Join-Path $work 'ca.pem') 'CERTIFICATE' $ca.RawData
    Write-Pem (Join-Path $work 'wrong-ca.pem') 'CERTIFICATE' $wrong.RawData
    Write-Pem (Join-Path $work 'server.pem') 'CERTIFICATE' $leaf.RawData
    Write-Pem (Join-Path $work 'server-key.pem') 'PRIVATE KEY' $leafKey.ExportPkcs8PrivateKey()
    & $Run $work
} finally {
    foreach($file in @('ca.pem','wrong-ca.pem','server.pem','server-key.pem')){
        $path=Join-Path $work $file
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}
    }
    foreach($resource in @($leaf,$ca,$wrong,$leafKey,$caKey,$wrongKey)){if($null -ne $resource){$resource.Dispose()}}
}

}
