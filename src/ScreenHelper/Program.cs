using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;

namespace ScreenHelper;

public class Settings {
  public bool Enabled { get; set; } = true;
  public bool WatchClipboard { get; set; } = true;
  public bool WatchFolder { get; set; } = true;
  public string Prompt { get; set; } = "Кратко опиши и объясни, что на скриншоте";
  public string Provider { get; set; } = "deepseek"; // deepseek | gigachat | api
  public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
  public string ApiKey { get; set; } = "";
  public string ApiModel { get; set; } = "gpt-4o-mini";
  public int HistorySize { get; set; } = 30;
}
public record Entry(DateTime Time, string Source, string Answer);

static class Program {
  [STAThread] static void Main() {
    using var mx = new Mutex(true, "ScreenHelper_single", out bool first);
    if (!first) { MessageBox.Show("Помощник по скриншотам уже запущен (значок в трее).", "Помощник"); return; }
    ApplicationConfiguration.Initialize();
    Application.Run(new TrayApp());
  }
}

class TrayApp : Form {
  public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenHelper");
  string SetFile => Path.Combine(Dir, "settings.json");
  string HistFile => Path.Combine(Dir, "history.json");
  static readonly JsonSerializerOptions JO = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
  public Settings S = new();
  public List<Entry> History = new();
  NotifyIcon tray; ToolStripMenuItem miEnabled;
  FileSystemWatcher fsw; ChatForm chat; HistoryForm hist;
  readonly SemaphoreSlim queue = new(1, 1);
  string lastHash = ""; DateTime lastClip = DateTime.MinValue;
  [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
  [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
  const int WM_CLIPBOARDUPDATE = 0x031D;

  public TrayApp() {
    Directory.CreateDirectory(Dir);
    try { if (File.Exists(SetFile)) S = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SetFile)) ?? new(); } catch { }
    try { if (File.Exists(HistFile)) History = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(HistFile)) ?? new(); } catch { }
    ShowInTaskbar = false; WindowState = FormWindowState.Minimized; FormBorderStyle = FormBorderStyle.FixedToolWindow; Opacity = 0; Size = new Size(1, 1);
    var icon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")!);
    Icon = icon;
    var m = new ContextMenuStrip();
    m.Items.Add("История ответов", null, (_, _) => ShowHistory());
    m.Items.Add("Окно ИИ-чата (вход в аккаунт)", null, (_, _) => chat.ShowChat());
    m.Items.Add("Настройки…", null, (_, _) => ShowSettings());
    miEnabled = new ToolStripMenuItem("Включено", null, (_, _) => { S.Enabled = !S.Enabled; Save(); UpdState(); });
    m.Items.Add(miEnabled);
    m.Items.Add("Отправить картинку из буфера сейчас", null, (_, _) => { if (Clipboard.ContainsImage()) OnShot(Clipboard.GetImage(), "буфер (вручную)", true); });
    m.Items.Add(new ToolStripSeparator());
    m.Items.Add("Выход", null, (_, _) => { tray.Visible = false; Application.Exit(); });
    tray = new NotifyIcon { Icon = icon, ContextMenuStrip = m, Visible = true };
    tray.DoubleClick += (_, _) => ShowHistory();
    tray.BalloonTipClicked += (_, _) => ShowHistory();
    chat = new ChatForm(this);
    UpdState(); SetupWatcher();
  }
  protected override void OnLoad(EventArgs e) {
    base.OnLoad(e); Hide();
    AddClipboardFormatListener(Handle);
    if (S.Provider != "api" && !File.Exists(Path.Combine(Dir, "first.ok"))) {
      File.WriteAllText(Path.Combine(Dir, "first.ok"), "1");
      chat.ShowChat();
      MessageBox.Show("Помощник работает в трее.\n\nВ открывшемся окне войдите в аккаунт " + (S.Provider == "gigachat" ? "GigaChat" : "DeepSeek") +
        " (один раз), затем окно можно закрыть — оно уйдёт в фон.\n\nДальше просто делайте скриншоты (Win+Shift+S или PrtScn) — ответ появится уведомлением и в «Истории».", "Помощник по скриншотам");
    } else chat.Warmup();
  }
  protected override void SetVisibleCore(bool v) => base.SetVisibleCore(IsHandleCreated ? false : v);
  protected override void WndProc(ref Message m) {
    if (m.Msg == WM_CLIPBOARDUPDATE && S.Enabled && S.WatchClipboard) {
      try { if (Clipboard.ContainsImage()) { var img = Clipboard.GetImage(); if (img != null) { lastClip = DateTime.Now; OnShot(img, "буфер обмена", false); } } } catch { }
    }
    base.WndProc(ref m);
  }
  void SetupWatcher() {
    fsw?.Dispose(); fsw = null;
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    if (!S.WatchFolder) return;
    try { Directory.CreateDirectory(dir); } catch { return; }
    fsw = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true, SynchronizingObject = this };
    foreach (var f in new[] { "*.png", "*.jpg", "*.jpeg" }) fsw.Filters.Add(f);
    fsw.Created += async (_, e) => {
      if (!S.Enabled) return;
      await Task.Delay(800);
      for (int i = 0; i < 10; i++) {
        try { using var fs = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); var img = Image.FromStream(fs); OnShot(new Bitmap(img), "файл " + e.Name, false); return; }
        catch { await Task.Delay(500); }
      }
    };
  }
  static byte[] Png(Image img) { using var ms = new MemoryStream(); img.Save(ms, ImageFormat.Png); return ms.ToArray(); }
  async void OnShot(Image img, string src, bool force) {
    byte[] png; try { png = Png(img); } catch { return; }
    var h = Convert.ToHexString(SHA1.HashData(png));
    if (!force && h == lastHash) return; // тот же скриншот (буфер + файл)
    if (!force && src.StartsWith("файл") && (DateTime.Now - lastClip).TotalSeconds < 5) return; // Win+Shift+S кладёт и в буфер, и в папку
    lastHash = h;
    await queue.WaitAsync();
    try {
      tray.Text = "Помощник: думаю…";
      string ans;
      try { ans = S.Provider == "api" ? await AskApi(png) : await chat.Ask(S.Provider, S.Prompt, png); }
      catch (Exception ex) { ans = "[Ошибка] " + ex.Message; }
      ans = (ans ?? "").Trim(); if (ans == "") ans = "[Пустой ответ]";
      History.Insert(0, new Entry(DateTime.Now, src, ans));
      while (History.Count > Math.Max(1, S.HistorySize)) History.RemoveAt(History.Count - 1);
      try { File.WriteAllText(HistFile, JsonSerializer.Serialize(History, JO)); } catch { }
      tray.ShowBalloonTip(15000, "Ответ ИИ", ans.Length > 250 ? ans[..250] + "…" : ans, ToolTipIcon.Info);
      hist?.Reload();
    } finally { UpdState(); queue.Release(); }
  }
  async Task<string> AskApi(byte[] png) {
    if (string.IsNullOrWhiteSpace(S.ApiKey)) return "[API] Не задан ключ. Откройте Настройки.";
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", S.ApiKey.Trim());
    var body = new { model = S.ApiModel, messages = new object[] { new { role = "user", content = new object[] {
      new { type = "text", text = S.Prompt },
      new { type = "image_url", image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(png) } } } } } };
    var r = await http.PostAsync(S.ApiUrl, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
    var t = await r.Content.ReadAsStringAsync();
    if (!r.IsSuccessStatusCode) return "[API " + (int)r.StatusCode + "] " + (t.Length > 300 ? t[..300] : t);
    using var d = JsonDocument.Parse(t);
    return d.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
  }
  public void Save() { try { File.WriteAllText(SetFile, JsonSerializer.Serialize(S, JO)); } catch { } }
  void UpdState() { miEnabled.Checked = S.Enabled; tray.Text = S.Enabled ? "Помощник по скриншотам: включён" : "Помощник по скриншотам: выключен"; }
  void ShowHistory() { if (hist == null || hist.IsDisposed) hist = new HistoryForm(this); hist.Reload(); hist.Show(); hist.Activate(); }
  void ShowSettings() {
    using var f = new SettingsForm(S);
    if (f.ShowDialog() == DialogResult.OK) { Save(); SetupWatcher(); UpdState(); }
  }
  protected override void Dispose(bool d) { try { RemoveClipboardFormatListener(Handle); } catch { } if (d) tray?.Dispose(); base.Dispose(d); }
}

class ChatForm : Form {
  static readonly Dictionary<string, string> Sites = new() { ["deepseek"] = "https://chat.deepseek.com/", ["gigachat"] = "https://giga.chat/" };
  readonly WebView2 ai = new() { Dock = DockStyle.Fill };
  readonly Label status = new() { Dock = DockStyle.Top, Height = 24, Text = "Войдите в аккаунт. Окно можно закрыть — оно продолжит работать в фоне.", TextAlign = ContentAlignment.MiddleLeft };
  string site = ""; Task init;
  public ChatForm(TrayApp app) {
    Text = "Помощник — окно ИИ-чата"; Size = new Size(1000, 750); StartPosition = FormStartPosition.CenterScreen; Icon = app.Icon;
    Controls.Add(ai); Controls.Add(status);
    FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
  }
  Task Init() => init ??= InitCore();
  async Task InitCore() {
    CreateControl(); var _ = Handle;
    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, Path.Combine(TrayApp.Dir, "WebView2"));
    await ai.EnsureCoreWebView2Async(env);
  }
  public async void Warmup() { try { await Init(); } catch { } }
  public async void ShowChat() {
    Show(); Activate();
    try { await Init(); if (site == "") { var p = ((TrayApp)Application.OpenForms[0]).S.Provider; if (p == "api") p = "deepseek"; site = p; ai.CoreWebView2.Navigate(Sites[p]); } }
    catch (Exception ex) { MessageBox.Show("Не удалось запустить WebView2: " + ex.Message + "\nУстановите Microsoft Edge WebView2 Runtime.", "Помощник"); }
  }
  static string js;
  static string Res(string n) => new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(n)!).ReadToEnd();
  static string Str(string json) { try { return JsonSerializer.Deserialize<string>(json) ?? ""; } catch { return json; } }
  async Task<string> Js(string expr) {
    js ??= Res("web/capture.js") + "\n" + Res("web/attach.js");
    await ai.CoreWebView2.ExecuteScriptAsync(js);
    return await ai.CoreWebView2.ExecuteScriptAsync(expr);
  }
  public async Task<string> Ask(string p, string prompt, byte[] png) {
    await Init();
    if (site != p) { site = p; ai.CoreWebView2.Navigate(Sites[p]); await Task.Delay(7000); }
    else { ai.CoreWebView2.Navigate(Sites[p]); await Task.Delay(5000); } // новый чат на каждый скриншот
    for (int i = 0; i < 15 && Str(await ai.CoreWebView2.ExecuteScriptAsync("document.readyState")) != "complete"; i++) await Task.Delay(1000);
    await Js("window.__pp.mark(" + JsonSerializer.Serialize(prompt) + ")");
    var a = Str(await Js("window.__shAttach(" + JsonSerializer.Serialize(Convert.ToBase64String(png)) + ",'screenshot.png').then(String)"));
    if (a == "noinput") return "[ИИ] Не найдено поле ввода. Откройте «Окно ИИ-чата» в меню трея и войдите в аккаунт.";
    await Task.Delay(5000); // загрузка картинки на сайт
    var res = Str(await Js("window.__pp.send(" + JsonSerializer.Serialize(prompt) + ").then(String)"));
    if (res == "noinput") return "[ИИ] Не найдено поле ввода. Войдите в аккаунт в окне ИИ-чата.";
    await Task.Delay(2000);
    if (Str(await Js("String(window.__pp.pending())")) == "true") { await Task.Delay(4000); await Js("window.__pp.clickLast()"); }
    string last = ""; int stable = 0;
    for (int i = 0; i < 240; i++) {
      await Task.Delay(1000);
      using var d = JsonDocument.Parse(Str(await Js("window.__pp.poll()")));
      var t = d.RootElement.GetProperty("text").GetString() ?? ""; var busy = d.RootElement.GetProperty("busy").GetBoolean();
      if (t.Length > 0 && t == last) stable++; else stable = 0; last = t;
      if (t.Length > 0 && ((!busy && stable >= 3) || stable >= 12)) return t;
    }
    return last.Length > 0 ? last : "[ИИ] Ответ не получен за 4 минуты. Откройте окно ИИ-чата и посмотрите.";
  }
}

class HistoryForm : Form {
  readonly TrayApp app; readonly ListBox list = new() { Dock = DockStyle.Left, Width = 230 };
  readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10) };
  public HistoryForm(TrayApp a) {
    app = a; Text = "Помощник — история ответов"; Size = new Size(760, 480); Icon = a.Icon; StartPosition = FormStartPosition.Manual;
    var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 10, wa.Bottom - Height - 10); TopMost = true;
    var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
    var copy = new Button { Text = "Копировать", Width = 110, Height = 28 };
    copy.Click += (_, _) => { if (text.Text != "") Clipboard.SetText(text.Text); };
    var clear = new Button { Text = "Очистить", Width = 100, Height = 28 };
    clear.Click += (_, _) => { app.History.Clear(); try { File.Delete(Path.Combine(TrayApp.Dir, "history.json")); } catch { } Reload(); };
    bar.Controls.Add(copy); bar.Controls.Add(clear);
    Controls.Add(text); Controls.Add(list); Controls.Add(bar);
    list.SelectedIndexChanged += (_, _) => { var i = list.SelectedIndex; text.Text = i >= 0 && i < app.History.Count ? app.History[i].Answer.Replace("\n", "\r\n") : ""; };
  }
  public void Reload() {
    list.BeginUpdate(); list.Items.Clear();
    foreach (var e in app.History) list.Items.Add(e.Time.ToString("dd.MM HH:mm:ss") + " — " + e.Source);
    list.EndUpdate(); if (list.Items.Count > 0) list.SelectedIndex = 0; else text.Text = "Пока пусто. Сделайте скриншот (Win+Shift+S).";
  }
}

class SettingsForm : Form {
  public SettingsForm(Settings s) {
    Text = "Помощник — настройки"; Size = new Size(560, 520); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
    var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoScroll = true };
    t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    void Row(string l, Control c) { t.Controls.Add(new Label { Text = l, AutoSize = true, Anchor = AnchorStyles.Left }); c.Dock = DockStyle.Fill; t.Controls.Add(c); }
    var en = new CheckBox { Text = "Включено", Checked = s.Enabled };
    var wc = new CheckBox { Text = "Следить за буфером обмена (Win+Shift+S, PrtScn)", Checked = s.WatchClipboard };
    var wf = new CheckBox { Text = "Следить за папкой «Изображения\\Снимки экрана»", Checked = s.WatchFolder };
    var pr = new TextBox { Text = s.Prompt, Multiline = true, Height = 80, ScrollBars = ScrollBars.Vertical };
    var prov = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    prov.Items.AddRange(new object[] { "DeepSeek (веб-чат, без ключа)", "GigaChat (веб-чат, без ключа)", "API с ключом (OpenAI-совместимый)" });
    prov.SelectedIndex = s.Provider == "gigachat" ? 1 : s.Provider == "api" ? 2 : 0;
    var url = new TextBox { Text = s.ApiUrl }; var key = new TextBox { Text = s.ApiKey, UseSystemPasswordChar = true }; var model = new TextBox { Text = s.ApiModel };
    var hn = new NumericUpDown { Minimum = 1, Maximum = 500, Value = Math.Clamp(s.HistorySize, 1, 500) };
    t.Controls.Add(en); t.SetColumnSpan(en, 2); t.Controls.Add(wc); t.SetColumnSpan(wc, 2); t.Controls.Add(wf); t.SetColumnSpan(wf, 2);
    Row("Запрос к ИИ:", pr); Row("Нейросеть:", prov); Row("API адрес:", url); Row("API ключ:", key); Row("API модель:", model); Row("Хранить ответов:", hn);
    var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Width = 110 }; var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 100 };
    var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft }; bar.Controls.Add(cancel); bar.Controls.Add(ok);
    Controls.Add(t); Controls.Add(bar); AcceptButton = ok; CancelButton = cancel;
    ok.Click += (_, _) => {
      s.Enabled = en.Checked; s.WatchClipboard = wc.Checked; s.WatchFolder = wf.Checked;
      s.Prompt = string.IsNullOrWhiteSpace(pr.Text) ? new Settings().Prompt : pr.Text.Trim();
      s.Provider = new[] { "deepseek", "gigachat", "api" }[prov.SelectedIndex];
      s.ApiUrl = url.Text.Trim(); s.ApiKey = key.Text.Trim(); s.ApiModel = model.Text.Trim(); s.HistorySize = (int)hn.Value;
    };
  }
}
