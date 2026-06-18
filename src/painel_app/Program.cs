using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutoPipCollector
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // modo "aplicador elevado": copia os arquivos da pasta pending p/ o jogo e sai (sem UI)
            if (args.Length > 0 && args[0] == "--apply")
            {
                Updater.ApplyPending();
                return;
            }

            // modo "trocador de exe": roda a partir do exe NOVO (na pasta update), espera o exe
            // antigo liberar e se copia por cima dele. args: --swap-exe "<caminho do exe instalado>"
            if (args.Length >= 2 && args[0] == "--swap-exe")
            {
                Updater.SwapExe(args[1]);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private const string VERSION = "2.6.9";   // corpos de inimigos em MISSOES (Fighter.OnCollect)

        private readonly WebView2 web = new WebView2();
        private readonly string gameDir;
        private readonly string cfgPath;
        private readonly string logPath;
        private long logPos;
        private System.Windows.Forms.Timer logTimer;
        private bool ready;
        private string exeSwapStaged;     // exe novo baixado, aplicado ao fechar
        private string exeSwapInstalled;  // exe atual a ser trocado

        public MainForm()
        {
            Text = "Auto-Pip Collector  v" + VERSION;
            ClientSize = new Size(700, 920);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(6, 13, 9);
            MinimumSize = new Size(560, 620);
            try
            {
                using Stream ist = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico");
                if (ist != null) Icon = new Icon(ist);
            }
            catch { }

            gameDir = FindGame();
            cfgPath = gameDir != null
                ? Path.Combine(gameDir, "BepInEx", "config", "coletor_vault.json")
                : Path.Combine(AppContext.BaseDirectory, "coletor_vault.json");
            logPath = gameDir != null ? Path.Combine(gameDir, "BepInEx", "LogOutput.log") : null;

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(6, 13, 9);
            Controls.Add(web);

            Load += async (s, e) => await InitAsync();
        }

        // ao fechar: se baixou um exe novo, dispara o trocador (roda do exe novo,
        // espera este processo sair e se copia por cima do exe instalado)
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try
            {
                if (!string.IsNullOrEmpty(exeSwapStaged) && File.Exists(exeSwapStaged) &&
                    !string.IsNullOrEmpty(exeSwapInstalled))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exeSwapStaged,
                        Arguments = "--swap-exe \"" + exeSwapInstalled + "\"",
                        UseShellExecute = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                    };
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch { }
        }

        // ---- barra de titulo no tema Pip-Boy (Windows 11) ----
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));                 // DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/Win11)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));                 // idem p/ Win10 1809 (atributo antigo)
                int caption = Bgr(10, 22, 15);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));              // DWMWA_CAPTION_COLOR
                int text = Bgr(70, 240, 138);
                DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));                 // DWMWA_TEXT_COLOR
                int border = Bgr(19, 69, 43);
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));               // DWMWA_BORDER_COLOR
            }
            catch { /* Windows mais antigo: ignora, fica a barra padrao */ }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private static int Bgr(int r, int g, int b) => r | (g << 8) | (b << 16);

        private async Task InitAsync()
        {
            try
            {
                string udf = Path.Combine(Path.GetTempPath(), "AutoPipCollectorWV2");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, udf);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Este programa precisa do 'Microsoft Edge WebView2 Runtime' (normalmente ja vem no Windows 10/11).\n\n" +
                    "Baixe gratis em:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n(secao 'Evergreen Standalone Installer')\n\n" +
                    "Detalhe tecnico: " + ex.Message,
                    "Falta um componente do Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
                return;
            }

            CoreWebView2 c = web.CoreWebView2;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.IsZoomControlEnabled = false;
            c.WebMessageReceived += OnWebMessage;
            c.NavigationCompleted += async (s, e) => await OnLoaded();

            c.NavigateToString(LoadHtml());
        }

        private static string LoadHtml()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using Stream st = asm.GetManifestResourceStream("painel.html");
            using StreamReader rd = new StreamReader(st, Encoding.UTF8);
            return rd.ReadToEnd();
        }

        private async Task OnLoaded()
        {
            if (ready) return;
            ready = true;

            string cfg = ReadConfigOrDefault();
            string js = "window.bootData(" + Js(cfg) + "," + Js(VERSION) + ");";
            await web.CoreWebView2.ExecuteScriptAsync(js);

            if (gameDir == null)
            {
                await PushLog("AVISO: pasta do Fallout Shelter nao encontrada");
            }
            StartLogTail();

            // checa atualizacoes em segundo plano (falha em silencio se offline)
            if (gameDir != null) _ = CheckUpdatesAsync();
        }

        // ---------- auto-update via GitHub ----------
        private async Task CheckUpdatesAsync()
        {
            try
            {
                Updater.Result r = await Updater.CheckAndStageAsync(gameDir, PushLog, ShowBanner);
                if (r == null) return;

                if (r.TsvApplied)
                {
                    await PushLog("\U0001F524  Traducao atualizada (" + r.ModVersion + ")");
                    await ShowBanner("ok", "Traducao atualizada para a versao mais recente.");
                }

                if (r.ExePending)
                {
                    // troca silenciosa ao fechar (exe nao se sobrescreve rodando)
                    exeSwapStaged = r.ExeStaged;
                    exeSwapInstalled = r.ExeInstalled;
                    await PushLog("⬇️  Atualizacao do programa baixada.");
                    await ShowBanner("ok", "Atualizacao do programa baixada - sera aplicada quando voce fechar esta janela.");
                }

                if (r.DllPending)
                {
                    // a DLL precisa de admin + jogo fechado.
                    if (Updater.GameRunning())
                    {
                        await ShowBanner("warn", "Atualizacao do coletor baixada. Feche o Fallout Shelter e abra este programa de novo para aplicar.");
                        await PushLog("⬇️  Atualizacao do mod baixada; feche o jogo p/ aplicar.");
                    }
                    else
                    {
                        // avisa ANTES do pedido de admin, pra nao assustar
                        DialogResult resp = MessageBox.Show(
                            "Encontrei uma atualizacao do coletor (versao " + r.ModVersion + ").\n\n" +
                            "Posso aplicar agora? Vai aparecer um pedido de permissao do Windows - e so clicar SIM.",
                            "Atualizacao disponivel", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                        if (resp == DialogResult.Yes && Updater.TryApplyElevated())
                        {
                            await ShowBanner("ok", "Coletor atualizado para a versao " + r.ModVersion + ". Pode abrir o jogo!");
                            await PushLog("✅  Mod atualizado para " + r.ModVersion + ".");
                        }
                        else if (resp == DialogResult.Yes)
                        {
                            await ShowBanner("warn", "Nao consegui aplicar agora. Vou tentar de novo quando voce reabrir o programa.");
                        }
                        else
                        {
                            await ShowBanner("warn", "Atualizacao adiada. Ela sera aplicada quando voce reabrir o programa.");
                        }
                    }
                }
            }
            catch { /* sem internet / GitHub fora: ignora */ }
        }

        // faixa de aviso visivel no painel (some sozinha)
        private async Task ShowBanner(string kind, string msg)
        {
            try
            {
                string js = "window.showBanner(" + Js(kind) + "," + Js(msg) + ");";
                await web.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch { }
        }

        // ---------- ponte: o painel manda {cmd:'save', cfg:{...}} ----------
        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string msg = e.TryGetWebMessageAsString();
                using JsonDocument doc = JsonDocument.Parse(msg);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("cmd", out JsonElement cmd) && cmd.GetString() == "save")
                {
                    string json = root.GetProperty("cfg").GetRawText();
                    SaveConfigAtomic(json);
                }
            }
            catch (Exception ex)
            {
                await PushLog("erro ao salvar: " + ex.Message);
            }
        }

        private void SaveConfigAtomic(string json)
        {
            string dir = Path.GetDirectoryName(cfgPath);
            Directory.CreateDirectory(dir);
            string tmp = cfgPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(cfgPath)) File.Replace(tmp, cfgPath, null);
            else File.Move(tmp, cfgPath);
        }

        private string ReadConfigOrDefault()
        {
            try { if (File.Exists(cfgPath)) return File.ReadAllText(cfgPath); }
            catch { }
            return "{ \"ativo\": true, \"recursos\": { \"energia\": true, \"comida\": true, \"agua\": true, \"nuka\": false, \"stimpak\": true, \"radaway\": true }, " +
                   "\"dwellers\": { \"levelup\": true, \"treinamento\": true, \"exploradores\": true, \"gravidas\": true, \"radio\": true }, " +
                   "\"estranho\": true, \"fabrica_quantum\": false, \"idioma\": \"en\", \"parametros\": { \"intervalo_ms\": 800, \"confianca\": 0.85, \"hotkey_pausa\": \"F8\" } }";
        }

        // ---------- log ao vivo: le o LogOutput.log do BepInEx ----------
        private void StartLogTail()
        {
            if (logPath == null) return;
            try { logPos = File.Exists(logPath) ? new FileInfo(logPath).Length : 0; } catch { logPos = 0; }
            logTimer = new System.Windows.Forms.Timer { Interval = 600 };
            logTimer.Tick += async (s, e) => await ReadNewLog();
            logTimer.Start();
        }

        private async Task ReadNewLog()
        {
            try
            {
                if (!File.Exists(logPath)) return;

                string novo;
                using (FileStream fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;                // tamanho real pelo handle aberto (sem cache de metadados do Windows)
                    if (len < logPos) logPos = 0;        // log recriado (jogo reiniciou)
                    if (len == logPos) return;
                    fs.Seek(logPos, SeekOrigin.Begin);
                    int count = (int)(len - logPos);
                    byte[] buf = new byte[count];
                    int read = fs.Read(buf, 0, count);
                    logPos = fs.Position;
                    novo = Encoding.UTF8.GetString(buf, 0, read);
                }

                foreach (string raw in novo.Split('\n'))
                {
                    string amigavel = Traduzir(raw);
                    if (amigavel != null) await PushLog(amigavel);
                }
            }
            catch { /* arquivo ocupado momentaneamente; tenta no proximo tick */ }
        }

        // converte uma linha do log do mod numa mensagem amigavel (ou null p/ ignorar)
        private static string Traduzir(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf(">>", StringComparison.Ordinal) < 0) return null;
            // eventos
            if (line.Contains("Estranho")) return "\U0001F575️  Estranho Misterioso coletado!";
            if (line.Contains("Parto")) return "\U0001F37C  Bebê coletado";
            if (line.Contains("Level-up")) return "⬆️  Level-up coletado";
            if (line.Contains("Treinamento")) return "\U0001F3CB️  Treino concluído";
            if (line.Contains("Armario")) return "\U0001F4E6  Armário de missão coletado";
            if (line.Contains("Corpo")) return "\U0001F480  Corpo de inimigo coletado";
            // recursos (Quantum antes de Nuka, e RadAway antes de Radio, p/ nao confundir)
            if (line.Contains("Quantum")) return "⭐  Nuka-Cola QUANTUM coletado!";
            if (line.Contains("RadAway")) return "☢️  RadAway coletado";
            if (line.Contains("Radio")) return "\U0001F4FB  Novo morador (rádio)";
            if (line.Contains("Stimpak")) return "\U0001F489  Stimpak coletado";
            if (line.Contains("Energia")) return "⚡  Energia coletada";
            if (line.Contains("Comida")) return "\U0001F34E  Comida coletada";
            if (line.Contains("Agua")) return "\U0001F4A7  Água coletada";
            if (line.Contains("Nuka")) return "\U0001F964  Nuka-Cola coletada";
            return null;   // 'bebe fechado' e outras linhas viram nada
        }

        private async Task PushLog(string msg)
        {
            try
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                string js = "window.addLog(" + Js(ts) + "," + Js(msg) + ");";
                await web.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch { }
        }

        private static string Js(string s) => JsonSerializer.Serialize(s);

        // ---------- acha a pasta do Fallout Shelter (Steam) ----------
        private static string FindGame()
        {
            string[] guesses =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Fallout Shelter",
                @"C:\Program Files\Steam\steamapps\common\Fallout Shelter",
                @"D:\Steam\steamapps\common\Fallout Shelter",
                @"D:\SteamLibrary\steamapps\common\Fallout Shelter",
                @"E:\SteamLibrary\steamapps\common\Fallout Shelter",
                @"E:\Steam\steamapps\common\Fallout Shelter",
            };
            foreach (string g in guesses)
                if (File.Exists(Path.Combine(g, "FalloutShelter.exe"))) return g;

            string vdf = @"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf";
            try
            {
                if (File.Exists(vdf))
                {
                    foreach (string line in File.ReadAllLines(vdf))
                    {
                        int i = line.IndexOf("\"path\"", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int q1 = line.IndexOf('"', i + 6);
                        int q2 = q1 >= 0 ? line.IndexOf('"', q1 + 1) : -1;
                        if (q1 < 0 || q2 < 0) continue;
                        string path = line.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
                        string cand = Path.Combine(path, "steamapps", "common", "Fallout Shelter");
                        if (File.Exists(Path.Combine(cand, "FalloutShelter.exe"))) return cand;
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ============================================================
    //  Updater - checa o version.json no GitHub e aplica updates.
    //  - ptbr.tsv  -> pasta config (gravavel p/ usuario): aplica direto, mod recarrega sozinho.
    //  - ColetorVault.dll -> pasta plugins (Program Files): precisa admin + jogo fechado.
    //    Baixa p/ uma pasta "pending" e aplica com auto-elevacao (--apply).
    // ============================================================
    internal static class Updater
    {
        // URL do manifesto. Pode ser trocada sem recompilar criando o arquivo
        // BepInEx/config/update_url.txt com a URL dentro.
        private const string DEFAULT_URL =
            "https://raw.githubusercontent.com/SEU_USUARIO/SEU_REPO/main/version.json";

        private static readonly HttpClient http = NewHttp();

        private static HttpClient NewHttp()
        {
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(300);   // 5min: o exe tem 66MB e a rede pode ser lenta
            c.DefaultRequestHeaders.Add("User-Agent", "AutoPipCollector");
            return c;
        }

        private static string PendingDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "AutoPipCollector", "pending");

        public class Result
        {
            public string ModVersion;
            public bool TsvApplied;
            public bool DllPending;
            public bool ExePending;
            public string ExeStaged;      // caminho do exe NOVO baixado (na pasta update)
            public string ExeInstalled;   // caminho do exe atual em execucao
        }

        public static bool GameRunning()
        {
            try { return System.Diagnostics.Process.GetProcessesByName("FalloutShelter").Length > 0; }
            catch { return false; }
        }

        private static string ExeUpdateDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "AutoPipCollector", "update");

        public static string CurrentExePath()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.MainModule.FileName;
        }

        // roda a partir do exe NOVO (na pasta update): se copia por cima do exe instalado
        // assim que o processo antigo libera o arquivo. Sem admin (fica em LocalAppData).
        public static void SwapExe(string installedPath)
        {
            try
            {
                string mySelf = CurrentExePath();
                if (string.Equals(mySelf, installedPath, StringComparison.OrdinalIgnoreCase)) return;
                for (int i = 0; i < 40; i++)   // ate ~20s esperando o exe antigo sair
                {
                    try { File.Copy(mySelf, installedPath, true); break; }
                    catch { System.Threading.Thread.Sleep(500); }
                }
            }
            catch { }
            // troca silenciosa: nao relanca. A pasta update e limpa no proximo launch.
        }

        private static string ManifestUrl(string gameDir)
        {
            try
            {
                string side = Path.Combine(gameDir, "BepInEx", "config", "update_url.txt");
                if (File.Exists(side))
                {
                    string u = File.ReadAllText(side).Trim();
                    if (u.StartsWith("http")) return u;
                }
            }
            catch { }
            return DEFAULT_URL;
        }

        public static async Task<Result> CheckAndStageAsync(string gameDir, Func<string, Task> log, Func<string, string, Task> banner = null)
        {
            string url = ManifestUrl(gameDir);
            if (url.Contains("SEU_USUARIO")) return null;   // ainda nao configurado: nao faz nada

            string json = await http.GetStringAsync(url);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string modVersion = root.TryGetProperty("mod_version", out JsonElement mv) ? mv.GetString() : "?";
            Result res = new Result { ModVersion = modVersion };

            if (!root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
                return res;

            // limpa um exe novo deixado por uma troca anterior (ja aplicado)
            try { if (Directory.Exists(ExeUpdateDir)) Directory.Delete(ExeUpdateDir, true); } catch { }

            List<(string src, string dest)> toElevate = new List<(string, string)>();

            foreach (JsonElement f in files.EnumerateArray())
            {
              try   // um arquivo que falha (ex.: Release ainda nao existe) nao quebra os outros
              {
                string name = f.GetProperty("name").GetString();
                string destFolder = f.GetProperty("dest").GetString();    // "config", "plugins" ou "exe"
                string sha = f.GetProperty("sha256").GetString().ToLowerInvariant();
                string furl = f.GetProperty("url").GetString();
                bool admin = f.TryGetProperty("admin", out JsonElement a) && a.GetBoolean();

                // ----- atualizacao do PROPRIO app (exe) -----
                if (destFolder == "exe")
                {
                    string installed = CurrentExePath();
                    if (Sha256(installed) == sha) continue;                // ja na versao certa
                    if (banner != null) await banner("info", "Atualizacao do programa encontrada - baixando (~66 MB), aguarde...");
                    if (log != null) await log("\U0001F50E  Atualizacao do app encontrada; baixando...");
                    Directory.CreateDirectory(ExeUpdateDir);
                    string newExe = Path.Combine(ExeUpdateDir, "AutoPipCollector.new.exe");
                    byte[] bin = await http.GetByteArrayAsync(furl);
                    File.WriteAllBytes(newExe, bin);
                    if (Sha256(newExe) != sha) { try { File.Delete(newExe); } catch { } continue; }
                    res.ExePending = true;
                    res.ExeStaged = newExe;
                    res.ExeInstalled = installed;
                    continue;
                }

                string dest = Path.Combine(gameDir, "BepInEx", destFolder, name);
                if (Sha256(dest) == sha) continue;                         // ja esta atualizado

                // baixa p/ temp e confere o hash antes de aplicar
                string tmp = Path.Combine(Path.GetTempPath(), name + ".apc_dl");
                byte[] data = await http.GetByteArrayAsync(furl);
                File.WriteAllBytes(tmp, data);
                if (Sha256(tmp) != sha) { try { File.Delete(tmp); } catch { } continue; }  // download corrompido

                if (!admin)
                {
                    // config e gravavel p/ usuario: aplica direto
                    try
                    {
                        File.Copy(tmp, dest, true);
                        res.TsvApplied = true;
                    }
                    catch { }
                    try { File.Delete(tmp); } catch { }
                }
                else
                {
                    // stage p/ aplicar elevado
                    Directory.CreateDirectory(PendingDir);
                    string staged = Path.Combine(PendingDir, name);
                    File.Copy(tmp, staged, true);
                    try { File.Delete(tmp); } catch { }
                    toElevate.Add((staged, dest));
                    res.DllPending = true;
                }
              }
              catch { /* arquivo indisponivel: tenta no proximo launch */ }
            }

            if (toElevate.Count > 0)
            {
                // grava a lista src->dest p/ o modo --apply
                var arr = new List<Dictionary<string, string>>();
                foreach (var p in toElevate) arr.Add(new Dictionary<string, string> { { "src", p.src }, { "dest", p.dest } });
                File.WriteAllText(Path.Combine(PendingDir, "apply.json"),
                    JsonSerializer.Serialize(arr), new UTF8Encoding(false));
            }

            return res;
        }

        // chamado no proximo launch (ou logo apos baixar, se o jogo estiver fechado)
        public static bool TryApplyElevated()
        {
            try
            {
                string applyList = Path.Combine(PendingDir, "apply.json");
                if (!File.Exists(applyList)) return false;
                if (GameRunning()) return false;

                string exe = Process_MainModule();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--apply",
                    UseShellExecute = true,
                    Verb = "runas"           // UAC: pede admin
                };
                var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit(15000);
                return !File.Exists(applyList);   // sumiu = aplicou
            }
            catch { return false; }
        }

        // roda elevado (Main --apply): copia tudo da apply.json e limpa
        public static void ApplyPending()
        {
            try
            {
                string applyList = Path.Combine(PendingDir, "apply.json");
                if (!File.Exists(applyList)) return;
                string json = File.ReadAllText(applyList);
                var arr = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                foreach (var item in arr)
                {
                    string src = item["src"], dest = item["dest"];
                    if (!File.Exists(src)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(src, dest, true);
                    try { File.Delete(src); } catch { }
                }
                try { File.Delete(applyList); } catch { }
            }
            catch { }
        }

        private static string Process_MainModule()
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            return p.MainModule.FileName;
        }

        private static string Sha256(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var sha = SHA256.Create();
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
