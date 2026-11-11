using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovaVpn;

public sealed partial class MainWindow
{
    private bool subscriptionReviewBusy;
    private bool profileAliasEditorBusy;
    private System.Windows.Threading.DispatcherTimer subscriptionRetirementTimer;
    private enum RetirementCleanupResult { None, Deferred, Applied, Retry }
    private bool CanCleanupRetiredProfiles() => !snapshotMode&&!closing&&!startupRecoveryFailed&&!multiServerApplyBusy&&!SubscriptionManager.IsUpdating&&!subscriptionReviewBusy&&!collectionEditorBusy&&!configuredFallbackBusy&&!startupConnecting&&!startupSettingsBusy&&!profileSwitchInProgress&&!gamingTransitionBusy&&gamingCancellation==null&&
        !OwnedWindows.Cast<Window>().Any(w=>w.IsVisible)&&core.Status!=CoreStatus.Connecting&&(!core.HasOwnedRuntime||!String.IsNullOrEmpty(core.ActiveProfileId));
    private IEnumerable<string> RetiredProfileOwners()
    {
        var ids=new HashSet<string>(workloadProxies?.ProfilesInUse??new string[0],StringComparer.Ordinal);
        ids.UnionWith(MultiServerSettings.ProfileReferences(state));
        if(core.HasOwnedRuntime && core.LastConnectedRouting?.MultiServer!=null)ids.UnionWith(core.LastConnectedRouting.MultiServer.ReferencedProfileIds);
        if(!String.IsNullOrEmpty(core.ActiveProfileId))ids.Add(core.ActiveProfileId);
        if((connectionRequested||core.HasOwnedRuntime||gamingSession!=null)&&!String.IsNullOrEmpty(state.FallbackRootProfileId))ids.Add(state.FallbackRootProfileId);
        if(gamingSession!=null){if(gamingSession.Profile!=null)ids.Add(gamingSession.Profile.Id);if(gamingSessions?.BaseWasConnected==true&&!String.IsNullOrEmpty(gamingSessions.BaseProfileId))ids.Add(gamingSessions.BaseProfileId);}
        return ids;
    }
    private bool RetiredOwnersUnchanged(IEnumerable<string> captured) => new HashSet<string>(captured,StringComparer.Ordinal).SetEquals(RetiredProfileOwners());
    private RetirementCleanupResult CleanupRetiredProfiles(Action<AppState> persist=null)
    {
        if(!CanCleanupRetiredProfiles())return RetirementCleanupResult.Deferred;
        var opened=state;
        try{var plan=SubscriptionRetirementPlan.Prepare(opened,RetiredProfileOwners());if(plan==null)return RetirementCleanupResult.None;plan.Apply(opened,RetiredProfileOwners,()=>ReferenceEquals(opened,state)&&CanCleanupRetiredProfiles(),persist??StateStore.Save);InvalidateGamingResults();return RetirementCleanupResult.Applied;}
        catch(Exception error){System.Diagnostics.Debug.WriteLine("Subscription retirement: "+error.GetType().Name);return RetirementCleanupResult.Retry;}
    }
    private void ScheduleRetiredProfileCleanup()
    {
        if(snapshotMode||closing||!state.Profiles.Any(p=>p!=null&&p.RetiredFromSubscription)){subscriptionRetirementTimer?.Stop();return;}
        if(subscriptionRetirementTimer==null){
            subscriptionRetirementTimer=new System.Windows.Threading.DispatcherTimer();
            subscriptionRetirementTimer.Tick+=delegate{
                subscriptionRetirementTimer.Stop();if(snapshotMode||closing)return;
                var result=CleanupRetiredProfiles();
                if(result==RetirementCleanupResult.Applied){try{if(!closing)RefreshCurrentPage();}catch(Exception error){System.Diagnostics.Debug.WriteLine("Retirement view: "+error.GetType().Name);}}
                else if(result==RetirementCleanupResult.Deferred||result==RetirementCleanupResult.Retry){subscriptionRetirementTimer.Interval=TimeSpan.FromSeconds(result==RetirementCleanupResult.Retry?30:2);subscriptionRetirementTimer.Start();}
            };
        }
        subscriptionRetirementTimer.Interval=TimeSpan.FromSeconds(2);subscriptionRetirementTimer.Start();
    }


    private void RenderSubscriptionCards(Panel parent)
    {
        foreach (var subscription in state.Subscriptions)
        {
            var content = new StackPanel();
            content.Children.Add(T(subscription.DisplayName, 12, TextColor, FontWeights.SemiBold));
            var quotaText = T(SubscriptionPresentation.Describe(subscription.Quota, DateTime.UtcNow), 10, Muted);
            quotaText.TextWrapping = TextWrapping.Wrap;
            quotaText.Margin = new Thickness(0, 6, 0, 6);
            content.Children.Add(quotaText);
            string status = subscription.LastSuccessUtc == default(DateTime) ? "Ещё не обновлялась" : "Обновлена " + subscription.LastSuccessUtc.ToLocalTime().ToString("g");
            if (!string.IsNullOrWhiteSpace(subscription.LastError)) status += " · " + subscription.LastError + " · повтор " + subscription.NextAttemptUtc.ToLocalTime().ToString("t");
            var statusText = T(status, 9, string.IsNullOrEmpty(subscription.LastError) ? Muted : Warning);
            statusText.TextWrapping = TextWrapping.Wrap; content.Children.Add(statusText);
            if (!string.IsNullOrWhiteSpace(subscription.LastChangeSummary)) content.Children.Add(T(subscription.LastChangeSummary, 9, Muted));
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var refresh = SmallButton("Проверить обновление");
            refresh.Margin = new Thickness(0, 0, 8, 6);
            refresh.Click += async delegate { await ReviewSubscriptionsAsync(subscription.Id); Navigate("servers"); };
            actions.Children.Add(refresh);
            var undo = SmallButton("Отменить последнее обновление");
            undo.Margin = new Thickness(0, 0, 8, 6); undo.IsEnabled = subscription.Previous != null;
            undo.Click += async delegate
            {
                if (snapshotMode || closing) return;
                if (!CanReviewSubscriptions()) { Toast("Дождитесь завершения обновления или подключения.", Warning); return; }
                var opened=state;string url=subscription.Url;
                subscriptionReviewBusy = true;
                try {
                    if(!IsSubscriptionReviewCurrent(opened,subscription,url))throw new InvalidOperationException("Подписка изменилась. Откройте её заново.");
                    string active=core.ActiveProfileId;int generation=core.RuntimeGeneration;var owners=RetiredProfileOwners().ToArray();var commit=SubscriptionCommitPlan.PrepareUndo(state,subscription.Id,active,owners);
                    if (MessageBox.Show(this, "Вернуть серверы и тариф до последнего обновления? Текущие локальные настройки существующих профилей сохранятся.", "Отмена обновления", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    commit.Apply(state,()=>IsSubscriptionReviewCurrent(opened,subscription,url)&&core.ActiveProfileId==active&&core.RuntimeGeneration==generation&&RetiredOwnersUnchanged(owners),StateStore.Save);
                    await ReconcileWorkloadsAsync();
                    if (closing) return;
                    if (workloadStopUnconfirmed) {
                        Navigate("workloads");
                        Toast("Откат сохранён, но остановка одного из локальных прокси не подтверждена. Повторите остановку.", Warning);
                    } else Navigate("servers");
                }
                catch (Exception ex) { if (!closing) Toast(ex.Message, Warning); }
                finally { subscriptionReviewBusy = false; }
            };
            actions.Children.Add(undo);
            var rename = SmallButton("Название подписки");rename.Name="SubscriptionRename"; rename.Margin = new Thickness(0, 0, 8, 6);
            rename.Click += delegate
            {
                if(!CanReviewSubscriptions())return;var opened=state;string url=subscription.Url;
                var dialog = new TextPromptWindow("Название подписки", "Название видно только на этом устройстве", subscription.DisplayName, state.Visual) { Owner = this };
                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Value)) SaveSubscriptionPreferences(opened,subscription,url,dialog.Value,subscription.QuotaNotificationsEnabled,subscription.WarningDays,subscription.WarningRemainingPercent);
            };
            actions.Children.Add(rename);
            var alerts = SmallButton(subscription.QuotaNotificationsEnabled ? "Напоминания: вкл." : "Напоминания: выкл.");
            alerts.Name="SubscriptionAlerts";alerts.Margin = new Thickness(0, 0, 8, 6);
            alerts.Click += delegate { if(!CanReviewSubscriptions())return;SaveSubscriptionPreferences(state,subscription,subscription.Url,subscription.Name,!subscription.QuotaNotificationsEnabled,subscription.WarningDays,subscription.WarningRemainingPercent); };
            actions.Children.Add(alerts);
            var thresholds = SmallButton("Пороги напоминаний");thresholds.Name="SubscriptionThresholds"; thresholds.Margin = new Thickness(0, 0, 8, 6);
            thresholds.Click += delegate
            {
                if(!CanReviewSubscriptions())return;var opened=state;string url=subscription.Url;
                var days = new TextPromptWindow("Срок подписки", "Предупреждать за сколько дней (1–365)", subscription.WarningDays.ToString(), state.Visual) { Owner = this };
                if (days.ShowDialog() != true||!IsSubscriptionReviewCurrent(opened,subscription,url)) return;
                var percent = new TextPromptWindow("Остаток трафика", "Предупреждать при остатке не более, % (1–99)", subscription.WarningRemainingPercent.ToString(), state.Visual) { Owner = this };
                if (percent.ShowDialog() != true) return;
                if (!int.TryParse(days.Value, out int d) || d < 1 || d > 365 || !int.TryParse(percent.Value, out int p) || p < 1 || p > 99)
                { Toast("Укажите число дней от 1 до 365 и процент от 1 до 99.", Warning); return; }
                SaveSubscriptionPreferences(opened,subscription,url,subscription.Name,subscription.QuotaNotificationsEnabled,d,p);
            };
            actions.Children.Add(thresholds);
            content.Children.Add(actions);
            parent.Children.Add(new Border { Child = content, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12),
                Background = new SolidColorBrush(Surface), BorderBrush = new SolidColorBrush(BorderColor), BorderThickness = new Thickness(1), CornerRadius = Radius(12) });
        }
    }

    private async Task ToggleProfileFavoriteAsync(VpnProfile profile,Button icon,Action<AppState> persist=null)
    {
        if(!CanReviewSubscriptions())return;var opened=state;
        try{
            var plan=ProfileMutationPlan.Favorite(opened,profile,!profile.IsFavorite);
            plan.Apply(()=>ReferenceEquals(opened,state)&&CanReviewSubscriptions(),RetiredProfileOwners,persist??StateStore.Save);
            InvalidateGamingResults();await AnimateIconPopAsync(icon);
            if(closing||!ReferenceEquals(opened,state))return;
            await ReconcileWorkloadsAsync();if(!closing&&ReferenceEquals(opened,state))Navigate("servers");
        }catch(Exception error){if(!closing)Toast(LogSanitizer.Sanitize(error.Message),Warning);}
    }
    private bool CanDeleteProfile() => CanReviewSubscriptions()&&(!core.HasOwnedRuntime||!String.IsNullOrEmpty(core.ActiveProfileId));
    private void DeleteProfile(VpnProfile profile,Action<AppState> persist=null)
    {
        if(!CanDeleteProfile())return;var opened=state;
        try{
            var plan=ProfileMutationPlan.Delete(opened,profile);
            plan.Apply(()=>ReferenceEquals(opened,state)&&CanDeleteProfile(),RetiredProfileOwners,persist??StateStore.Save);
            InvalidateGamingResults();Navigate("servers");
        }catch(Exception error){if(!closing)Toast(LogSanitizer.Sanitize(error.Message),Warning);}
    }

    private void RenameProfile(VpnProfile profile)
    {
        if(!CanReviewSubscriptions())return;
        var opened=state;
        try{
            var plan=ProfileAliasCommitPlan.Capture(opened,profile);
            profileAliasEditorBusy=true;
            var dialog=new TextPromptWindow("Название сервера","Введите понятное название профиля",profile.Name,state.Visual){Owner=this};
            if(dialog.ShowDialog()!=true||String.IsNullOrWhiteSpace(dialog.Value))return;
            profileAliasEditorBusy=false;
            plan.Apply(dialog.Value,()=>ReferenceEquals(opened,state)&&CanReviewSubscriptions(),StateStore.Save);
            InvalidateGamingResults();Navigate("servers");
        }catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}
        finally{profileAliasEditorBusy=false;ScheduleRetiredProfileCleanup();}
    }

    private void SaveSubscriptionPreferences(AppState opened,SubscriptionRecord record,string url,string name,bool notifications,int days,int remainingPercent)
    {
        if(snapshotMode||closing)return;
        try{
            if(!CanReviewSubscriptions()||!IsSubscriptionReviewCurrent(opened,record,url))throw new InvalidOperationException("Подписка изменилась. Откройте её заново.");
            var commit=SubscriptionCommitPlan.PreparePreferences(state,record.Id,name,notifications,days,remainingPercent);
            commit.Apply(state,()=>CanReviewSubscriptions()&&IsSubscriptionReviewCurrent(opened,record,url),StateStore.Save);
            Navigate("servers");
        }catch(Exception error){Toast(LogSanitizer.Sanitize(error.Message),Warning);}
    }
    private bool ConfirmSubscriptionPlan(SubscriptionChangePlan plan, IEnumerable<string> warnings)
    {
        var preview = new ImportPreviewWindow(plan.Imported, state.Visual, plan.Summary) { Owner = this };
        return preview.ShowDialog() == true;
    }

    private bool CanReviewSubscriptions(bool ownsReview=false) => !snapshotMode&&!closing&&(ownsReview||!subscriptionReviewBusy)&&!profileAliasEditorBusy&&!multiServerApplyBusy&&!SubscriptionManager.IsUpdating&&!configuredFallbackBusy&&!startupConnecting&&!startupSettingsBusy&&!profileSwitchInProgress&&!gamingTransitionBusy&&core.Status!=CoreStatus.Connecting;
    private bool CanCommitBackgroundSubscriptions(AppState opened,string active,int generation) => Object.ReferenceEquals(opened,state)&&!snapshotMode&&!closing&&!subscriptionReviewBusy&&!profileAliasEditorBusy&&!multiServerApplyBusy&&!collectionEditorBusy&&!configuredFallbackBusy&&!startupConnecting&&!startupSettingsBusy&&!profileSwitchInProgress&&!gamingTransitionBusy&&core.Status!=CoreStatus.Connecting&&core.ActiveProfileId==active&&core.RuntimeGeneration==generation;
    private bool IsSubscriptionReviewCurrent(AppState opened,SubscriptionRecord record,string url) => Object.ReferenceEquals(opened,state)&&CanReviewSubscriptions(true)&&state.Subscriptions.Any(item=>Object.ReferenceEquals(item,record)&&item.Url==url);

    private async Task ReviewSubscriptionsAsync(string subscriptionId = null)
    {
        if(snapshotMode||closing)return;
        if(!CanReviewSubscriptions()){Toast("Дождитесь завершения обновления или подключения.",Warning);return;}
        var opened=state;
        subscriptionReviewBusy = true;
        try
        {
            foreach (var sourceId in opened.Subscriptions.Where(s => subscriptionId == null || s.Id == subscriptionId).Select(s=>s.Id).ToArray())
            {
                var subscription=opened.Subscriptions.FirstOrDefault(s=>s.Id==sourceId);if(subscription==null)return;
                bool applied=false;string url=subscription.Url;
                try
                {
                    if(!IsSubscriptionReviewCurrent(opened,subscription,url))return;
                    var attemptUtc=DateTime.UtcNow;ImportResult imported;
                    try{imported=await LinkParser.ImportWithCancellationAsync(url,mapHealthCancellation.Token);}
                    catch(Exception error){
                        if(!mapHealthCancellation.IsCancellationRequested&&IsSubscriptionReviewCurrent(opened,subscription,url)){
                            var failed=SubscriptionCommitPlan.PrepareFailure(state,subscription.Id,url,error,attemptUtc,DateTime.UtcNow);
                            failed.Apply(state,()=>IsSubscriptionReviewCurrent(opened,subscription,url),StateStore.Save);
                        }
                        throw;
                    }
                    if(!IsSubscriptionReviewCurrent(opened,subscription,url))return;
                    var plan = SubscriptionManager.Preview(state, imported);
                    if (!ConfirmSubscriptionPlan(plan, imported.Warnings.Concat(imported.RoutingPreviews.Select(p => p.Summary)))) continue;
                    if(!IsSubscriptionReviewCurrent(opened,subscription,url))return;
                    var owners=RetiredProfileOwners().ToArray();
                    var commit=SubscriptionCommitPlan.Prepare(state,plan,core.ActiveProfileId,owners);
                    commit.Apply(state,()=>IsSubscriptionReviewCurrent(opened,subscription,url)&&RetiredOwnersUnchanged(owners),StateStore.Save);applied=true;
                    await ReconcileWorkloadsAsync();
                    Toast(plan.Summary, plan.IsPartial ? Warning : Green);
                }
                catch (OperationCanceledException) { if (!closing) Toast(applied?"Обновление прервано после изменения профилей. Проверьте локальные прокси перед повторным запуском.":"Проверка отменена или истекло время ожидания.", Warning);if(applied)return; }
                catch (Exception) { if(!closing)Toast(applied?"Не удалось завершить обновление подписки. Изменения могли не сохраниться. Проверьте локальные прокси и повторите обновление.":"Не удалось проверить подписку «" + subscription.DisplayName + "». Существующие серверы сохранены.", Warning);if(applied)return; }
            }
            NotifySubscriptionQuota(); SyncSubscriptionTimer();
        }
        finally { subscriptionReviewBusy = false; }
    }

    private void NotifySubscriptionQuota()
    {
        if(snapshotMode||closing)return;var opened=state;
        try{SubscriptionQuotaNotifier.CheckAndSave(opened,DateTime.UtcNow,TryShowNotification,StateStore.Save,()=>!snapshotMode&&!closing&&Object.ReferenceEquals(opened,state));}
        catch(Exception error){System.Diagnostics.Debug.WriteLine("Quota notification: "+error.GetType().Name);}
    }
}

public static class SubscriptionPresentation
{
    public static string Bytes(long value)
    {
        string[] units = { "Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ", "ЭиБ" };
        double size = Math.Max(0L, value); int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Describe(SubscriptionQuota quota, DateTime now)
    {
        if (quota == null || !quota.HasValues) return "Провайдер не предоставил остаток трафика и срок действия.";
        string usage = quota.RemainingBytes.HasValue ? "Осталось " + Bytes(quota.RemainingBytes.Value) + " из " + Bytes(quota.TotalBytes.Value) :
            quota.UsedBytes.HasValue ? "Использовано " + Bytes(quota.UsedBytes.Value) + " · лимит не указан" : "Остаток трафика не предоставлен";
        string expiry = quota.ExpiresUtc.HasValue ? " · " + (quota.ExpiresUtc <= now ? "срок истёк " : "действует до ") + quota.ExpiresUtc.Value.ToLocalTime().ToString("d") : " · срок не указан";
        string freshness = "\nСведения провайдера от " + quota.ReceivedUtc.ToLocalTime().ToString("g") + (now - quota.ReceivedUtc > TimeSpan.FromHours(48) ? " · могут быть устаревшими" : "");
        return usage + expiry + freshness + (quota.Warnings?.Count > 0 ? "\nЧасть данных тарифа некорректна." : "");
    }

    public static string Alert(SubscriptionRecord record, DateTime now, out string key)
    {
        key = ""; var quota = record.Quota;
        if (quota == null || !record.QuotaNotificationsEnabled) return "";
        if (quota.ExpiresUtc.HasValue && quota.ExpiresUtc <= now.AddDays(Math.Max(1, Math.Min(365, record.WarningDays))))
        {
            key = "expiry:" + quota.ExpiresUtc.Value.Ticks + ":" + (quota.ExpiresUtc <= now ? "expired" : "soon");
            return quota.ExpiresUtc <= now ? "срок действия истёк" : "срок действия заканчивается " + quota.ExpiresUtc.Value.ToLocalTime().ToString("d");
        }
        if (quota.RemainingBytes.HasValue && quota.TotalBytes > 0 && (double)quota.RemainingBytes.Value / quota.TotalBytes.Value <= Math.Max(1, Math.Min(99, record.WarningRemainingPercent)) / 100.0)
        {
            key = "quota:" + quota.TotalBytes.Value + ":" + (quota.RemainingBytes == 0 ? "empty" : "low");
            return "осталось " + Bytes(quota.RemainingBytes.Value) + " трафика";
        }
        return "";
    }
}
