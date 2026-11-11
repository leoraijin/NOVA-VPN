using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NovaVpn;

public sealed class ApplicationBandwidthPolicyEdit
{
    public bool Enabled{get;}
    public ReadOnlyCollection<ApplicationBandwidthPolicy> Policies{get;}
    public ApplicationBandwidthPolicyEdit(bool enabled,IEnumerable<ApplicationBandwidthRule> rules)
    {
        if(rules==null)throw new ArgumentNullException(nameof(rules));var rows=rules.Take(ApplicationBandwidthPolicySnapshot.MaximumRules+1).ToList();
        if(enabled&&rows.Count==0)throw new ArgumentException("Добавьте приложение и задайте скорость либо выключите лимиты.");
        if(rows.Count>ApplicationBandwidthPolicySnapshot.MaximumRules)throw new ArgumentException("Поддерживается не более 1024 правил. Удалите лишние.");
        var candidate=new AppState{ApplicationLimitsEnabled=enabled||rows.Count!=0,ApplicationBandwidthRules=rows};
        Policies=ApplicationBandwidthPolicySnapshot.Freeze(candidate).Policies;Enabled=enabled;
    }
    internal void WriteTo(AppState state){state.ApplicationLimitsEnabled=Enabled;state.ApplicationBandwidthRules=Policies.Select(p=>new ApplicationBandwidthRule{UserSid=p.Identity.UserSid,ExecutablePath=p.Identity.ExecutablePath,UploadBytesPerSecond=p.UploadBytesPerSecond,DownloadBytesPerSecond=p.DownloadBytesPerSecond,BurstBytes=p.BurstBytes}).ToList();}
    // Includes malformed/null rows without validating or erasing them, allowing
    // an editor to repair the exact source it opened and reject stale saves.
    public static string Fingerprint(AppState state)
    {
        if(state==null)throw new ArgumentNullException(nameof(state));
        using(var hash=SHA256.Create())using(var stream=new CryptoStream(Stream.Null,hash,CryptoStreamMode.Write)){
            using(var writer=new BinaryWriter(stream,new UTF8Encoding(false,true),true)){
                writer.Write((byte)1);writer.Write(state.ApplicationLimitsEnabled);var rows=state.ApplicationBandwidthRules;writer.Write(rows==null?-1:rows.Count);
                if(rows!=null)foreach(var row in rows){writer.Write(row!=null);if(row==null)continue;Write(writer,row.UserSid);Write(writer,row.ExecutablePath);writer.Write(row.UploadBytesPerSecond);writer.Write(row.DownloadBytesPerSecond);writer.Write(row.BurstBytes);}
            }
            stream.FlushFinalBlock();return Convert.ToBase64String(hash.Hash);
        }
    }
    static void Write(BinaryWriter writer,string value){writer.Write(value!=null);if(value!=null){writer.Write(value.Length);foreach(char code in value)writer.Write((ushort)code);}}
    public static string FormatMegabits(long bytes)=>((decimal)bytes/125000m).ToString("0.######",CultureInfo.CurrentCulture);
    public static long ParseMegabits(string text)
    {
        decimal value;if(!Decimal.TryParse((text??"").Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)||value<0||value>ApplicationBandwidthBudget.MaximumBytesPerSecond/125000m)throw new ArgumentException("Укажите скорость числом от 0 до 8 000 000 Мбит/с, например 10 или 2,5.");
        decimal bytes=value*125000m;if(bytes!=Decimal.Truncate(bytes))throw new ArgumentException("Минимальный шаг скорости — 0,000008 Мбит/с.");return (long)bytes;
    }
}
