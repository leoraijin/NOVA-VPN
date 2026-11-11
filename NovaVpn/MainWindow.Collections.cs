using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private bool collectionEditorBusy;
    private List<VpnProfile> CollectionCandidates() => ProfileCollectionService.Candidates(state);

    private Task StartAllowedProfileAsync(VpnProfile profile, RoutingSettings routing)
    {
        if (collectionEditorBusy) throw new InvalidOperationException("Закройте редактор коллекции перед подключением.");
        if(routing?.MultiServer!=null)
            return core.StartAsync(MultiServerStartupSelection.Resolve(state,routing),routing,state.Profiles);
        string error = ProfileCollectionService.Exclusion(state, profile);
        if (error != null) throw new InvalidOperationException(error);
        return core.StartAsync(profile, routing, state.Profiles);
    }

    private VpnProfile CollectionBestAvailable(string excludedId = null) => ProfileCollectionService.BestAvailableFor(state, DateTime.UtcNow,
        ProfileHealthEvidence.NetworkKey(), ProfileHealthEvidence.CoreKey(core.ProbeCorePath), excludedId);

    private bool CanChangeCollection(bool ownsEditor=false) => !snapshotMode&&!closing&&(ownsEditor||!collectionEditorBusy)&&!subscriptionReviewBusy&&!SubscriptionManager.IsUpdating&&!configuredFallbackBusy&&!profileSwitchInProgress&&!startupConnecting&&!startupSettingsBusy&&core.CanSelectEngine&&gamingSession==null&&gamingCancellation==null;

    private UIElement CollectionBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        bar.Children.Add(new TextBlock { Text = "Коллекция для выбора: ", Foreground = new SolidColorBrush(TextColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 6) });
        var opened=state;var selector = new ComboBox { Name="ProfileCollectionSelector", Width = 240, Margin = new Thickness(0, 0, 8, 6), Style = Application.Current.Resources["NovaSelectorStyle"] as Style };
        selector.Items.Add(new ComboBoxItem { Content = "Все профили", Tag = "" });
        foreach (var collection in state.ProfileCollections ?? new List<ProfileCollection>()) selector.Items.Add(new ComboBoxItem { Content = collection.Name, Tag = collection.Id });
        var active = selector.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (state.ActiveProfileCollectionId ?? ""));
        if (active == null) { active = new ComboBoxItem { Content = "Коллекция недоступна", Tag = state.ActiveProfileCollectionId }; selector.Items.Add(active); }
        selector.SelectedItem = active;
        selector.SelectionChanged += async delegate {
            if(snapshotMode||closing)return;
            try{
                if(!CanChangeCollection()||!Object.ReferenceEquals(opened,state))throw new InvalidOperationException("Завершите подключения и активные операции перед сменой коллекции.");
                var plan=CollectionSelectionPlan.Prepare(state,(selector.SelectedItem as ComboBoxItem)?.Tag as string);
                plan.Apply(state,()=>CanChangeCollection()&&Object.ReferenceEquals(opened,state),StateStore.Save);
                manualDisconnect=true;connectionRequested=false;reconnectTimer.Stop();InvalidateGamingResults();
                await ReconcileWorkloadsAsync();
            }catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}
            if(!closing&&currentPage=="servers")Navigate("servers");
        };
        bar.Children.Add(selector);
        var edit = SmallButton("Настроить коллекцию"); edit.Margin = new Thickness(0, 0, 8, 6);
        edit.Click += async delegate { await EditCollection(false); }; bar.Children.Add(edit);
        var create = SmallButton("Новая коллекция"); create.Margin = new Thickness(0, 0, 8, 6);
        create.Click += async delegate { await EditCollection(true); }; bar.Children.Add(create);
        ProfileCollectionService.Active(state, out string error);
        if (error != null) bar.Children.Add(new TextBlock { Text = error, Foreground = new SolidColorBrush(Warning), TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
        return bar;
    }

    private async Task EditCollection(bool create)
    {
        if(snapshotMode||closing)return;
        if(!CanChangeCollection()){Toast("Завершите подключения и активные операции перед изменением коллекций.",Warning);return;}
        var opened=state;string baseline=FullStateRestorePlan.Fingerprint(opened);int generation=core.RuntimeGeneration;
        var collection=create?null:(state.ProfileCollections??new List<ProfileCollection>()).FirstOrDefault(c=>c.Id==state.ActiveProfileCollectionId);
        if(!create&&collection==null){Toast("Выберите коллекцию для редактирования.",Warning);return;}
        collectionEditorBusy=true;
        try{
            var editor=new ProfileCollectionWindow(collection,state.Profiles,state.Visual){Owner=this};
            if(editor.ShowDialog()!=true)return;
            if(!CanChangeCollection(true)||!Object.ReferenceEquals(opened,state)||core.RuntimeGeneration!=generation)throw new InvalidOperationException("Состояние клиента изменилось. Откройте коллекцию заново.");
            var plan=CollectionSelectionPlan.PrepareEdit(state,baseline,collection?.Id,editor.Accepted,editor.AcceptedCountries,editor.Deleted);
            plan.Apply(state,()=>CanChangeCollection(true)&&Object.ReferenceEquals(opened,state)&&core.RuntimeGeneration==generation,StateStore.Save);
            manualDisconnect=true;connectionRequested=false;reconnectTimer.Stop();fallbackFailedProfileId=null;InvalidateGamingResults();
            await ReconcileWorkloadsAsync();
            if(workloadStopUnconfirmed)Toast("Коллекция сохранена, но остановка одного из прокси не подтверждена. Повторите остановку.",Warning);
        }catch(Exception error){if(!closing)Toast(LogSanitizer.Sanitize(error.Message),Warning);}
        finally{collectionEditorBusy=false;if(!closing&&currentPage=="servers")Navigate("servers");}
    }
    private async Task ProbeCollectionForStartupAsync(CancellationToken token)
    {
        using (var slots = new SemaphoreSlim(2))
        {
            await Task.WhenAll(CollectionCandidates().Select(async profile => {
                await slots.WaitAsync(token);
                try
                {
                    string key = SubscriptionManager.ConfigKey(profile);
                    var result = await ServerMapHealthService.TestAsync(profile, core.ProbeCorePath,
                        System.IO.Path.Combine(StateStore.DirectoryPath, "runtime", "startup-probes"), token);
                    token.ThrowIfCancellationRequested();
                    if (key == SubscriptionManager.ConfigKey(profile)) ProfileHealthEvidence.Record(profile, result);
                }
                finally { slots.Release(); }
            }));
        }
    }
}
