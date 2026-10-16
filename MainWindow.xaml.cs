using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WifiManager;

public partial class MainWindow : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly Config _cfg;
    RouterOsClient? _ros;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };

    public MainWindow(Config cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        HostBox.Text = cfg.Host; PortBox.Text = cfg.Port; UserBox.Text = cfg.User;
        PassBox.Password = cfg.Password; ShopBox.Text = cfg.Shop;
        WanBox.Text = cfg.WanInterface; CodeLenBox.Text = cfg.CodeLen.ToString(Inv);
        _timer.Tick += async (_, _) => { try { await SnapshotAsync(); } catch { } };
        Loaded += (_, _) => RefreshLocal();
    }

    // ================= أدوات مساعدة =================
    static bool IsNetErr(Exception ex) =>
        ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is OperationCanceledException;

    async Task ConnectAsync()
    {
        _ros?.Dispose();
        int port = int.TryParse(_cfg.Port, out var p) ? p : 8728;
        var c = new RouterOsClient(_cfg.Host, port);
        await c.ConnectAsync(_cfg.User, _cfg.Password);
        _ros = c;
        StatusText.Text = $"متصل بـ {_cfg.Host}";
    }

    async Task<List<Dictionary<string, string>>> R(string cmd, Dictionary<string, string>? attrs = null,
        string[]? queries = null, int timeoutSec = 20)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (_ros == null || !_ros.Connected) await ConnectAsync();
                return await _ros!.CallAsync(cmd, attrs, queries, timeoutSec);
            }
            catch (Exception ex) when (IsNetErr(ex))
            {
                _ros?.Dispose(); _ros = null;
                StatusText.Text = "غير متصل";
                if (attempt >= 2) throw;
            }
        }
    }

    async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (RouterOsException ex) { MessageBox.Show(ex.Message, "خطأ من الراوتر", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch (Exception ex) when (IsNetErr(ex)) { MessageBox.Show("تعذر الاتصال بالراوتر. تحقق من العنوان والمنفذ وتفعيل API.\n" + ex.Message, "خطأ اتصال"); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "خطأ"); }
    }

    static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
    static long GetL(Dictionary<string, string> d, string k) => long.TryParse(Get(d, k), out var v) ? v : 0;
    static double ParseD(string s)
    {
        if (!double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out var v) || v <= 0)
            throw new Exception($"قيمة غير صحيحة: {s}");
        return v;
    }
    static string FmtBytes(long n)
    {
        double d = n; string[] u = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return $"{d:0.0} {u[i]}";
    }
    static string UptimeStr(double hours)
    {
        int m = Math.Max(1, (int)Math.Round(hours * 60));
        return $"{m / 60}h{m % 60}m";
    }

    // ================= الاتصال =================
    async void SaveConnect_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Host = HostBox.Text.Trim(); _cfg.Port = PortBox.Text.Trim(); _cfg.User = UserBox.Text.Trim();
        _cfg.Password = PassBox.Password; _cfg.Shop = ShopBox.Text.Trim(); _cfg.WanInterface = WanBox.Text.Trim();
        _cfg.CodeLen = int.TryParse(CodeLenBox.Text, out var n) ? Math.Clamp(n, 4, 16) : 8;
        if (!string.IsNullOrEmpty(AppPassBox.Password)) { _cfg.AppPassHash = Config.MakeHash(AppPassBox.Password); AppPassBox.Clear(); }
        _cfg.Save();
        _ros?.Dispose(); _ros = null;
        await Guard(async () =>
        {
            await ConnectAsync();
            var id = await R("/system/identity/print");
            Db.Log("اتصال", $"{_cfg.Host} / {(id.Count > 0 ? Get(id[0], "name") : "")}");
            MessageBox.Show("تم الاتصال بنجاح بالراوتر: " + (id.Count > 0 ? Get(id[0], "name") : ""));
        });
    }

    void ClearAppPass_Click(object sender, RoutedEventArgs e)
    {
        _cfg.AppPassHash = ""; _cfg.Save();
        Db.Log("إزالة قفل البرنامج");
        MessageBox.Show("تمت إزالة قفل البرنامج");
    }

    // ================= عام =================
    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.Source, Tabs)) return;
        RefreshLocal();
    }

    void RefreshLocal()
    {
        var plans = Db.Plans();
        PlansGrid.ItemsSource = plans;
        PlanCombo.ItemsSource = plans;
        if (PlanCombo.SelectedIndex < 0 && plans.Count > 0) PlanCombo.SelectedIndex = 0;
        CardsGrid.ItemsSource = Db.Cards();
        RefreshReport();
        LogGrid.ItemsSource = Db.LogRows();
        RefreshLoss();
    }

    // ================= الباقات =================
    void AddPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PlanNameBox.Text.Trim();
            if (name == "") throw new Exception("اكتب اسم الباقة");
            Db.AddPlan(name, ParseD(PlanHoursBox.Text), ParseD(PlanPriceBox.Text));
            Db.Log("إضافة باقة", name);
            PlanNameBox.Clear(); PlanHoursBox.Clear(); PlanPriceBox.Clear();
            RefreshLocal();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    void DelPlan_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in PlansGrid.SelectedItems.Cast<Plan>().ToList()) { Db.DeletePlan(p.Id); Db.Log("حذف باقة", p.Name); }
        RefreshLocal();
    }

    // ================= توليد الكروت =================
    string NewCode()
    {
        while (true)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _cfg.CodeLen; i++) sb.Append(RandomNumberGenerator.GetInt32(10));
            var code = sb.ToString();
            if (!Db.CodeExists(code)) return code;
        }
    }

    async Task<string> EnsureProfile(double hours)
    {
        var name = $"plan-{(int)Math.Round(hours * 60)}m";
        var found = await R("/ip/hotspot/user/profile/print", null, new[] { "?name=" + name });
        if (found.Count == 0)
            await R("/ip/hotspot/user/profile/add", new() { ["name"] = name, ["shared-users"] = "1", ["keepalive-timeout"] = "2m" });
        return name;
    }

    async void Generate_Click(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            if (PlanCombo.SelectedItem is not Plan plan) throw new Exception("اختر باقة");
            if (!int.TryParse(CountBox.Text, out var n) || n < 1 || n > 500) throw new Exception("العدد بين 1 و 500");
            var profile = await EnsureProfile(plan.Hours);
            var made = new List<string>();
            for (int i = 0; i < n; i++)
            {
                var code = NewCode();
                await R("/ip/hotspot/user/add", new()
                {
                    ["name"] = code, ["password"] = code, ["profile"] = profile,
                    ["limit-uptime"] = UptimeStr(plan.Hours), ["comment"] = "card:" + plan.Name
                });
                Db.AddCard(code, plan.Name, plan.Hours, plan.Price);
                made.Add(code);
            }
            Db.Log("توليد كروت", $"{n} × {plan.Name}");
            RefreshLocal();
            if (PrintCheck.IsChecked == true) PrintCards(Db.Cards().Where(c => made.Contains(c.Code)).ToList());
            else MessageBox.Show($"تم توليد {n} كرت");
        });
    }

    // ================= الكروت =================
    void RefreshCards_Click(object sender, RoutedEventArgs e) => RefreshLocal();

    List<Card> SelectedCards() => CardsGrid.SelectedItems.Cast<Card>().ToList();

    void MarkSold_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in SelectedCards()) { Db.MarkSold(c.Code); Db.Log("بيع كرت", $"{c.Code} - {c.PlanName} - {c.Price}"); }
        RefreshLocal();
    }

    void PrintSel_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedCards();
        if (sel.Count == 0) { MessageBox.Show("لم تحدد كروتًا"); return; }
        PrintCards(sel);
    }

    void PrintUnsold_Click(object sender, RoutedEventArgs e)
    {
        var rows = Db.Cards(true);
        if (rows.Count == 0) { MessageBox.Show("لا توجد كروت متاحة"); return; }
        PrintCards(rows);
    }

    async void DelCards_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedCards();
        var deletable = sel.Where(c => !c.Sold).ToList();
        int skipped = sel.Count - deletable.Count;
        if (deletable.Count == 0) { MessageBox.Show("لا توجد كروت غير مباعة في التحديد (الكروت المباعة محمية من الحذف)"); return; }
        if (MessageBox.Show($"حذف {deletable.Count} كرت من الراوتر والبرنامج؟" + (skipped > 0 ? $"\n(سيتم تجاهل {skipped} كرت مباع)" : ""),
            "تأكيد", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Guard(async () =>
        {
            foreach (var c in deletable)
            {
                var users = await R("/ip/hotspot/user/print", null, new[] { "?name=" + c.Code });
                foreach (var u in users) await R("/ip/hotspot/user/remove", new() { [".id"] = Get(u, ".id") });
                Db.DeleteCard(c.Code);
            }
            Db.Log("حذف كروت", string.Join(",", deletable.Select(c => c.Code)));
            RefreshLocal();
        });
    }

    void PrintCards(List<Card> rows)
    {
        var shop = WebUtility.HtmlEncode(_cfg.Shop);
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append($"<div class='c'><div class='s'>{shop}</div><div class='p'>{WebUtility.HtmlEncode(r.PlanName)}</div>" +
                      $"<div class='code'>{r.Code}</div><div class='s'>السعر: {r.Price.ToString("0.##", Inv)}</div></div>");
        var html = "<!doctype html><html dir='rtl' lang='ar'><meta charset='utf-8'><title>كروت</title><style>" +
                   "body{font-family:Tahoma,Arial;margin:10px}.g{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}" +
                   ".c{border:1.5px dashed #333;padding:8px;text-align:center;break-inside:avoid}" +
                   ".code{font-size:22px;font-weight:bold;letter-spacing:2px;margin:6px 0;direction:ltr}" +
                   ".p{font-size:15px}.s{font-size:12px;color:#444}</style>" +
                   $"<body onload='window.print()'><div class='g'>{sb}</div></body></html>";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wifi_cards.html");
        System.IO.File.WriteAllText(path, html, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ================= المتصلون =================
    async void RefreshActive_Click(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            var rows = await R("/ip/hotspot/active/print");
            ActiveGrid.ItemsSource = rows.Select(a => new ActiveRow
            {
                Id = Get(a, ".id"), User = Get(a, "user"), Address = Get(a, "address"), Mac = Get(a, "mac-address"),
                Uptime = Get(a, "uptime"), TimeLeft = Get(a, "session-time-left"),
                Down = FmtBytes(GetL(a, "bytes-out")), Up = FmtBytes(GetL(a, "bytes-in"))
            }).ToList();
        });
    }

    async void Kick_Click(object sender, RoutedEventArgs e)
    {
        var sel = ActiveGrid.SelectedItems.Cast<ActiveRow>().ToList();
        if (sel.Count == 0) return;
        await Guard(async () =>
        {
            foreach (var a in sel) await R("/ip/hotspot/active/remove", new() { [".id"] = a.Id });
            Db.Log("فصل مشترك", string.Join(",", sel.Select(a => a.User)));
            RefreshActive_Click(sender, e);
        });
    }

    // ================= الفاقد =================
    async Task SnapshotAsync()
    {
        var ifs = await R("/interface/print", null, new[] { "?name=" + _cfg.WanInterface });
        if (ifs.Count == 0) throw new RouterOsException($"الواجهة '{_cfg.WanInterface}' غير موجودة. صحّح اسم واجهة WAN في تبويب الاتصال.");
        long wan = GetL(ifs[0], "rx-byte") + GetL(ifs[0], "tx-byte");
        var users = await R("/ip/hotspot/user/print");
        long hs = users.Sum(u => GetL(u, "bytes-in") + GetL(u, "bytes-out"));
        Db.AddTraffic(wan, hs);
        RefreshLoss();
    }

    void RefreshLoss()
    {
        var snaps = Db.Traffic();
        var rows = new List<LossRow>();
        double sumWan = 0, sumHs = 0;
        for (int i = 1; i < snaps.Count; i++)
        {
            long dW = snaps[i].wan - snaps[i - 1].wan, dH = snaps[i].hs - snaps[i - 1].hs;
            if (dW < 0 || dH < 0) continue; // إعادة تشغيل الراوتر أو حذف مستخدمين: تجاهل الفترة
            double w = dW / 1048576.0, h = dH / 1048576.0;
            rows.Add(new LossRow { Time = snaps[i].ts, WanMB = w, HotspotMB = h, LossMB = w - h, LossPct = w > 0 ? (w - h) / w * 100 : 0 });
            sumWan += w; sumHs += h;
        }
        rows.Reverse();
        LossGrid.ItemsSource = rows;
        if (rows.Count == 0) { LossSummary.Text = "لا توجد بيانات كافية بعد. خذ لقطتين على الأقل بينهما استخدام للإنترنت."; return; }
        double loss = sumWan - sumHs, pct = sumWan > 0 ? loss / sumWan * 100 : 0;
        LossSummary.Text = $"الإجمالي: WAN = {sumWan:N0} MB | الكروت = {sumHs:N0} MB | الفاقد = {loss:N0} MB ({pct:0.#}%)" +
                           (pct > 15 ? "\n⚠ النسبة مرتفعة: افحص الـ bypass والمنافذ السلكية في تبويب الفحص الأمني." : "");
    }

    async void SnapNow_Click(object sender, RoutedEventArgs e) =>
        await Guard(async () => { await SnapshotAsync(); Db.Log("لقطة ترافيك"); });

    void RefreshLoss_Click(object sender, RoutedEventArgs e) => RefreshLoss();

    void AutoMon_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoMon.IsChecked == true) _timer.Start(); else _timer.Stop();
    }

    // ================= الفحص الأمني =================
    async Task<List<Dictionary<string, string>>> Safe(string cmd)
    {
        try { return await R(cmd); } catch (RouterOsException) { return new(); }
    }

    async void RunAudit_Click(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            AuditBox.Text = "جارٍ الفحص...";
            var find = new List<string>();
            var sb = new StringBuilder();
            void Sec(string t) { sb.AppendLine(); sb.AppendLine("=== " + t + " ==="); }

            var users = await R("/user/print");
            Sec("مستخدمو الراوتر");
            foreach (var u in users)
            {
                sb.AppendLine($"{Get(u, "name")} | group={Get(u, "group")} | address={Get(u, "address")} | آخر دخول={Get(u, "last-logged-in")}");
                if (Get(u, "address") == "") find.Add($"مستخدم الراوتر '{Get(u, "name")}' بلا قيد عنوان (يستطيع الدخول من أي مكان). تأكد أنك تعرفه.");
            }
            find.Add($"عدد مستخدمي الراوتر = {users.Count}. أي اسم لا تعرفه احذفه فورًا.");

            Sec("الجلسات الإدارية النشطة");
            foreach (var a in await Safe("/user/active/print"))
                sb.AppendLine($"{Get(a, "name")} | من {Get(a, "address")} | عبر {Get(a, "via")} | منذ {Get(a, "when")}");

            Sec("الخدمات");
            foreach (var s in await Safe("/ip/service/print"))
            {
                bool off = Get(s, "disabled") == "true";
                var nm = Get(s, "name");
                sb.AppendLine($"{nm} | port={Get(s, "port")} | {(off ? "معطّلة" : "مفعّلة")} | address={Get(s, "address")}");
                if (off) continue;
                if (nm is "telnet" or "ftp" or "www") find.Add($"الخدمة '{nm}' مفعّلة وغير آمنة. عطّلها.");
                else if (nm is "api" or "winbox" or "ssh" && Get(s, "address") == "") find.Add($"الخدمة '{nm}' مفتوحة لكل العناوين. قيّدها بعنوان جهازك.");
            }

            Sec("السكربتات");
            var scripts = await Safe("/system/script/print");
            foreach (var s in scripts) sb.AppendLine($"{Get(s, "name")} | owner={Get(s, "owner")} | {Trunc(Get(s, "source"), 150)}");
            foreach (var s in scripts) find.Add($"سكربت '{Get(s, "name")}': راجع محتواه وتأكد أنك من أنشأه.");

            Sec("المجدولات (Scheduler)");
            var scheds = await Safe("/system/scheduler/print");
            foreach (var s in scheds) sb.AppendLine($"{Get(s, "name")} | interval={Get(s, "interval")} | {Trunc(Get(s, "on-event"), 150)}");
            foreach (var s in scheds) find.Add($"مجدول '{Get(s, "name")}': راجع ما ينفذه.");

            Sec("Netwatch");
            var nw = await Safe("/tool/netwatch/print");
            foreach (var s in nw) { sb.AppendLine($"{Get(s, "host")} | up={Get(s, "up-script")} | down={Get(s, "down-script")}"); find.Add($"Netwatch على {Get(s, "host")}: راجع السكربتات."); }

            Sec("Hotspot IP-Binding");
            foreach (var b in await Safe("/ip/hotspot/ip-binding/print"))
            {
                sb.AppendLine($"type={Get(b, "type")} | mac={Get(b, "mac-address")} | addr={Get(b, "address")} | {Get(b, "comment")}");
                if (Get(b, "type") == "bypassed") find.Add($"IP-binding bypassed (يتجاوز الدفع): MAC={Get(b, "mac-address")} ADDR={Get(b, "address")} ({Get(b, "comment")})");
            }

            Sec("Walled Garden");
            var wg = await Safe("/ip/hotspot/walled-garden/print");
            var wgIp = await Safe("/ip/hotspot/walled-garden/ip/print");
            foreach (var w in wg) sb.AppendLine($"{Get(w, "action")} | host={Get(w, "dst-host")} | {Get(w, "comment")}");
            foreach (var w in wgIp) sb.AppendLine($"[ip] {Get(w, "action")} | dst={Get(w, "dst-address")} | {Get(w, "comment")}");
            if (wg.Count + wgIp.Count > 0) find.Add($"Walled Garden فيه {wg.Count + wgIp.Count} قاعدة: مواقع متاحة بلا كرت. راجعها.");

            Sec("قواعد NAT (غير التلقائية)");
            foreach (var n in await Safe("/ip/firewall/nat/print"))
            {
                if (Get(n, "dynamic") == "true") continue;
                sb.AppendLine($"{Get(n, "chain")} | {Get(n, "action")} | in={Get(n, "in-interface")} out={Get(n, "out-interface")} | dst={Get(n, "dst-address")}:{Get(n, "dst-port")} to={Get(n, "to-addresses")}");
                if (Get(n, "action") is "dst-nat" or "redirect" or "netmap") find.Add($"قاعدة NAT '{Get(n, "action")}' غير تلقائية: {Get(n, "comment")} {Get(n, "dst-port")} -> {Get(n, "to-addresses")}");
            }

            // مطابقة الكروت بين الراوتر والبرنامج
            Sec("مطابقة الكروت");
            var hsUsers = await R("/ip/hotspot/user/print");
            var db = Db.Cards();
            var dbCodes = db.Select(c => c.Code).ToHashSet();
            var routerOnly = hsUsers.Where(u => Get(u, "name") != "default-trial" && !dbCodes.Contains(Get(u, "name"))).ToList();
            var noLimit = hsUsers.Where(u => Get(u, "name") != "default-trial" && Get(u, "limit-uptime") == "" && Get(u, "limit-bytes-total") == "").ToList();
            var byName = hsUsers.ToDictionary(u => Get(u, "name"), u => u, StringComparer.Ordinal);
            var usedNotSold = db.Where(c => !c.Sold && byName.TryGetValue(c.Code, out var u) &&
                (Get(u, "uptime") is not ("" or "0s") || GetL(u, "bytes-in") > 0)).ToList();
            var missingOnRouter = db.Where(c => !byName.ContainsKey(c.Code)).ToList();
            sb.AppendLine($"مستخدمو الهوتسبوت في الراوتر: {hsUsers.Count} | كروت البرنامج: {db.Count}");
            if (routerOnly.Count > 0) { sb.AppendLine("في الراوتر فقط: " + string.Join(", ", routerOnly.Take(40).Select(u => Get(u, "name")))); find.Add($"{routerOnly.Count} مستخدم هوتسبوت موجود في الراوتر ولم يُنشأ من البرنامج (كروت خارج الحساب)."); }
            if (noLimit.Count > 0) find.Add($"{noLimit.Count} مستخدم هوتسبوت بلا حد وقت ولا حجم (إنترنت مفتوح بلا نهاية).");
            if (usedNotSold.Count > 0) { sb.AppendLine("مستخدمة ولم تُسجّل كمبيعات: " + string.Join(", ", usedNotSold.Take(40).Select(c => c.Code))); find.Add($"{usedNotSold.Count} كرت استُخدم فعليًا ولم يُسجّل كمبيع (فاقد مالي محتمل = {usedNotSold.Sum(c => c.Price):0.##})."); }
            if (missingOnRouter.Count > 0) find.Add($"{missingOnRouter.Count} كرت في البرنامج غير موجود في الراوتر (حُذف من الراوتر خارج البرنامج؟).");

            var head = new StringBuilder();
            head.AppendLine($"تقرير الفحص - {Db.Now()}");
            head.AppendLine(find.Count == 0 ? "لا توجد ملاحظات." : $"ملاحظات تحتاج مراجعتك ({find.Count}):");
            int k = 1;
            foreach (var f in find) head.AppendLine($"  {k++}. {f}");
            AuditBox.Text = head.ToString() + sb;
            Db.Log("فحص أمني", $"{find.Count} ملاحظة");
        });
    }

    static string Trunc(string s, int n) => s.Length <= n ? s.Replace("\n", " ") : s.Substring(0, n).Replace("\n", " ") + "…";

    async void BackupRouter_Click(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            var name = "wifi-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv);
            await R("/system/backup/save", new() { ["name"] = name }, null, 60);
            await R("/export", new() { ["file"] = name }, null, 60);
            Db.Log("نسخة احتياطية", name);
            MessageBox.Show($"تم إنشاء {name}.backup و {name}.rsc داخل الراوتر.\nحمّلهما من Winbox ← Files إلى جهازك.");
        });
    }

    void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AuditBox.Text)) { MessageBox.Show("شغّل الفحص أولًا"); return; }
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"audit-{DateTime.Now.ToString("yyyyMMdd-HHmm", Inv)}.txt");
        System.IO.File.WriteAllText(path, AuditBox.Text, new UTF8Encoding(true));
        MessageBox.Show("تم الحفظ: " + path);
    }

    // ================= التقارير =================
    void RefreshReport()
    {
        var today = Db.SoldTotals(DateTime.Now.ToString("yyyy-MM-dd", Inv));
        var month = Db.SoldTotals(DateTime.Now.ToString("yyyy-MM", Inv));
        var all = Db.SoldTotals("");
        ReportSummary.Text = $"اليوم: {today.n} كرت | {today.sum:0.##}\nهذا الشهر: {month.n} كرت | {month.sum:0.##}\nالإجمالي: {all.n} كرت | {all.sum:0.##}";
        ReportGrid.ItemsSource = Db.ReportByDay();
    }
}
