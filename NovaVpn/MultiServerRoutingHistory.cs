using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace NovaVpn;

// AppState owns this history; RoutingSettings has no history member, so snapshots cannot recurse.
[DataContract]
public sealed class MultiServerRoutingHistory
{
    [DataMember] public RoutingSettings SingleServerRouting;
    [DataMember] public string SelectedProfileId;
    [DataMember] public string FallbackRootProfileId;
    [DataMember(IsRequired=true)] public MultiServerSettings SavedAssignments;
    public void Validate() {
        if(SavedAssignments==null)throw new SerializationException("Нет сохранённых назначений.");
        SavedAssignments.Validate();
        if(SingleServerRouting!=null) {
            if(SingleServerRouting.MultiServer!=null||SingleServerRouting.Gaming!=null)throw new SerializationException("Неверный снимок обычной маршрутизации.");
            SingleServerRouting.MigratedPolicy?.Validate();
        }
        foreach(string id in new[]{SelectedProfileId,FallbackRootProfileId})if(id!=null&&(String.IsNullOrWhiteSpace(id)||id.Length>128))throw new SerializationException("Неверный сервер снимка маршрутизации.");
    }
    [OnDeserialized] void Read(StreamingContext context) {Validate();}
    public MultiServerRoutingHistory Copy() {
        Validate();return new MultiServerRoutingHistory{SingleServerRouting=SingleServerRouting==null?null:RoutingOverlayService.Clone(SingleServerRouting),
            SelectedProfileId=SelectedProfileId,FallbackRootProfileId=FallbackRootProfileId,SavedAssignments=SavedAssignments.Copy()};
    }
    public static string Fingerprint(MultiServerRoutingHistory value) {
        if(value==null)return "none";value.Validate();
        using(var stream=new MemoryStream())using(var sha=SHA256.Create()) {
            new DataContractJsonSerializer(typeof(MultiServerRoutingHistory)).WriteObject(stream,value);return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
        }
    }
    public static MultiServerRoutingHistory CaptureForAssignments(AppState current,RoutingSettings candidate) {
        if(current==null||candidate?.MultiServer==null)throw new ArgumentNullException();
        var history=current.MultiServerRoutingHistory?.Copy();
        if(current.Routing?.MultiServer==null)history=new MultiServerRoutingHistory{SingleServerRouting=AssignmentEditorSource(current),
            SelectedProfileId=String.IsNullOrEmpty(current.SelectedProfileId)?null:current.SelectedProfileId,
            FallbackRootProfileId=String.IsNullOrEmpty(current.FallbackRootProfileId)?null:current.FallbackRootProfileId};
        if(history==null)history=new MultiServerRoutingHistory(); // Older enabled documents never had a previous snapshot.
        history.SavedAssignments=candidate.MultiServer.Copy();history.Validate();return history;
    }
    public static MultiServerRoutingHistory PreviewDisable(AppState current) {
        if(current?.Routing?.MultiServer==null)throw new InvalidOperationException("Назначения уже отключены.");
        var history=current.MultiServerRoutingHistory?.Copy();
        if(history?.SingleServerRouting==null) {
            var single=RoutingOverlayService.Clone(current.Routing);single.MultiServer=null;
            history=new MultiServerRoutingHistory{SingleServerRouting=single,SelectedProfileId=current.Routing.MultiServer.DefaultProfileId,
                FallbackRootProfileId=current.Routing.MultiServer.DefaultProfileId};
        }
        history.SavedAssignments=current.Routing.MultiServer.Copy();history.Validate();return history;
    }
    public static bool MissingRestoredProfile(AppState state) => state?.Routing?.MultiServer==null&&state?.MultiServerRoutingHistory!=null&&
        !String.IsNullOrEmpty(state.SelectedProfileId)&&!state.Profiles.Any(p=>p.Id==state.SelectedProfileId&&!p.RetiredFromSubscription);
    internal static RoutingSettings AssignmentEditorSource(AppState current) =>
        current.Routing?.MultiServer == null && ZapretRoutingTransitionService.HasActiveRouting(current)
            ? ZapretRoutingTransitionService.WithoutLegacyRouting(current).Routing
            : RoutingOverlayService.Clone(current.Routing);

    public static RoutingSettings PreviewResume(AppState current) {
        if(current==null||current.Routing?.MultiServer!=null||current.MultiServerRoutingHistory==null)throw new InvalidOperationException("Нет отключённых назначений.");
        var history=current.MultiServerRoutingHistory.Copy();var routes=AssignmentEditorSource(current);routes.MultiServer=history.SavedAssignments.Copy();return routes;
    }
}
