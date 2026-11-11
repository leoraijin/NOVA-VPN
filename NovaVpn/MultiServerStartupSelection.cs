using System;
using System.Collections.Generic;
using System.Linq;

namespace NovaVpn;

// A retained missing default identifies the policy; it is not an available exit.
public static class MultiServerStartupSelection
{
    public static VpnProfile Resolve(AppState state,RoutingSettings routing)
    {
        if(state==null||routing?.MultiServer==null)throw new ArgumentException("Нет назначений серверов.");
        routing.MultiServer.Validate();
        if(routing.MultiServer.RequiresRebinding)throw new InvalidOperationException("Заново назначьте серверы для импортированных правил.");
        var profile = ResolveCatalogIdentity(routing.MultiServer, state.Profiles);
        // A retired ID never contributes an outbound, so an ordinary availability
        // exclusion must not prevent other assigned exits or explicit rejects.
        if(!profile.RetiredFromSubscription){string error=ProfileCollectionService.Exclusion(state,profile);if(error!=null)throw new InvalidOperationException(error);}
        return profile;
    }

    // The identity can be absent from the catalog. Never add it to the outbound snapshot.
    internal static VpnProfile ResolveCatalogIdentity(MultiServerSettings settings, IEnumerable<VpnProfile> profiles) =>
        profiles.SingleOrDefault(p => p.Id == settings.DefaultProfileId) ??
        new VpnProfile { Id = settings.DefaultProfileId, Name = "Основной сервер недоступен", RetiredFromSubscription = true };
}
