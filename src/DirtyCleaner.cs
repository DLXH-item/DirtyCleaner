// 脏数据本地归档工具 v2.0 —— Windows 原生桌面程序（WinForms）
// 全盘/注册表枚举所有软件；点开软件后按需后台扫描，虚拟列表渲染，不卡；
// 每个软件分 全部文件 / 按数据分类 / 按文件类型；排序、多选、风险标注、删除拦截、
// 归档隔离与还原、打开文件所在位置。单文件 exe，无需安装任何运行环境。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

namespace DirtyArchiver
{
    // ---------------------------------------------------------------- 规则
    static class Rules
    {
        public const string PROTECTED = "protected";
        public const string CAUTION = "caution";
        public const string SAFE = "safe";

        public const string CACHE = "缓存临时";
        public const string LOG = "日志崩溃";
        public const string CONFIG = "配置数据";
        public const string PROG = "程序文件";
        public const string BACKUP = "备份归档";
        public const string OTHER = "其他";

        static readonly string[] CacheSegs = { "cache", "caches", "cache2", "temp", "tmp",
            "thumb", "thumbnails", "crashpad", "gpucache", "code cache", "shadercache", "blob_storage" };
        static readonly string[] LogSegs = { "log", "logs", "logfile", "logfiles", "crash",
            "crashes", "dump", "dumps", "minidump", "minidumps", "crashdumps", "crashreports" };
        static readonly string[] ConfigSegs = { "config", "configs", "settings", "setting",
            "preferences", "preference", "profile", "profiles", "save", "saves", "savegame",
            "savegames", "userdata", "database", "databases" };

        static readonly Dictionary<string, string> ExtCat = new Dictionary<string, string>
        {
            { ".log", LOG }, { ".tmp", CACHE }, { ".temp", CACHE }, { ".cache", CACHE },
            { ".dmp", LOG }, { ".mdmp", LOG }, { ".crash", LOG },
            { ".bak", BACKUP }, { ".old", BACKUP }, { ".backup", BACKUP }, { ".orig", BACKUP },
            { ".zip", BACKUP }, { ".rar", BACKUP }, { ".7z", BACKUP }, { ".tar", BACKUP },
            { ".gz", BACKUP }, { ".tgz", BACKUP }, { ".bz2", BACKUP },
            { ".ini", CONFIG }, { ".json", CONFIG }, { ".yaml", CONFIG }, { ".yml", CONFIG },
            { ".toml", CONFIG }, { ".xml", CONFIG }, { ".cfg", CONFIG }, { ".conf", CONFIG },
            { ".db", CONFIG }, { ".sqlite", CONFIG }, { ".sqlite3", CONFIG }, { ".dat", CONFIG },
            { ".sav", CONFIG }, { ".save", CONFIG },
            { ".exe", PROG }, { ".dll", PROG }, { ".sys", PROG }, { ".so", PROG },
            { ".bat", PROG }, { ".cmd", PROG }, { ".msi", PROG }, { ".lnk", PROG }
        };

        static readonly string[] DocExts = { ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
            ".pdf", ".psd", ".ai", ".cdr", ".py", ".c", ".cpp", ".h", ".java", ".vue", ".cs", ".js" };

        public static readonly string[] AllCats = { CACHE, LOG, CONFIG, PROG, BACKUP, OTHER };

        public static string Category(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            string c;
            if (ExtCat.TryGetValue(ext, out c))
            {
                if (c == PROG || c == BACKUP) return c;
            }
            string[] segs = rel.ToLowerInvariant().Split(new char[] { '\\', '/' });
            for (int i = 0; i < segs.Length; i++)
            {
                string s = segs[i].Trim();
                if (In(CacheSegs, s)) return CACHE;
                if (In(LogSegs, s)) return LOG;
                if (In(ConfigSegs, s)) return CONFIG;
            }
            if (c != null) return c;
            return OTHER;
        }

        public static string Risk(string rel, string category, string ext)
        {
            if (In(DocExts, ext)) return PROTECTED;
            if (category == PROG || category == CONFIG) return PROTECTED;
            if (category == CACHE || category == LOG) return SAFE;
            return CAUTION;
        }

        static bool In(string[] arr, string v)
        {
            foreach (string a in arr) if (a == v) return true;
            return false;
        }

        public static string RiskText(string risk)
        {
            if (risk == PROTECTED) return "● 影响使用";
            if (risk == SAFE) return "● 可清理";
            return "● 谨慎清理";
        }

        public static Color RiskColor(string risk)
        {
            if (risk == PROTECTED) return Color.FromArgb(192, 57, 43);
            if (risk == SAFE) return Color.FromArgb(24, 128, 74);
            return Color.FromArgb(176, 112, 12);
        }

        public static int RiskRank(string risk)
        {
            if (risk == PROTECTED) return 0;
            if (risk == CAUTION) return 1;
            return 2;
        }

        public static string SizeText(long n)
        {
            if (n < 1024) return n + " B";
            double d = n;
            string[] u = { "KB", "MB", "GB", "TB", "PB" };
            foreach (string uu in u)
            {
                d /= 1024.0;
                if (d < 1024) return d.ToString("F1") + " " + uu;
            }
            return d.ToString("F1") + " EB";
        }

        public static string ExtLabel(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return "(无扩展名)";
            return ext.TrimStart('.');
        }
    }

    // ---------------------------------------------------------------- 数据
    class Rec
    {
        public string Soft;
        public string Name;
        public string Rel;
        public string Full;
        public string Ext;
        public string Category;
        public string Risk;
        public long SizeBytes;
        public DateTime MTime;
    }

    class Soft
    {
        public string Name;
        public List<string> Paths = new List<string>();
        public List<Rec> Files = new List<Rec>();
        public long TotalSize;
        public int State;          // 0 未扫描 1 扫描中 2 完成
        public bool Truncated;
        public string From;        // 来源说明：Registry / Program Files / Local …

        public void AddPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            p = p.TrimEnd('\\', '/');
            if (p.Length == 0) return;
            foreach (string x in Paths)
                if (string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) return;
            Paths.Add(p);
        }

        public string PathText
        {
            get
            {
                if (Paths.Count == 0) return "-";
                if (Paths.Count == 1) return Paths[0];
                return Paths[0] + " 等 " + Paths.Count + " 处";
            }
        }

        // 列表里用的紧凑位置：C:\…\末级目录
        public string DisplayPath
        {
            get
            {
                if (Paths.Count == 0) return "-";
                string p = Paths[0];
                if (p.Length > 24)
                {
                    string root = null, leaf = null;
                    try { root = Path.GetPathRoot(p); leaf = Path.GetFileName(p.TrimEnd('\\')); }
                    catch { }
                    if (!string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(leaf) && root.Length <= 4)
                        p = root.TrimEnd('\\') + "\\…\\" + leaf;
                }
                if (Paths.Count > 1) p = p + " 等" + Paths.Count + "处";
                return p;
            }
        }
    }

    class CatChoice
    {
        public string Name;
        public string Label;
        public int Count;

        public override string ToString()
        {
            return Label + "  (" + Count.ToString("N0") + ")";
        }
    }

    // ---------------------------------------------------------------- 软件枚举
    static class SoftFinder
    {
        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windows", "$recycle.bin", "system volume information", "recovery", "config.msi",
            "perflogs", "msocache", "documents and settings", "program files", "program files (x86)",
            "programdata", "users", "windows.old", "$winreagent", "$sysreset", "boot", "efi",
            "intel", "amd", "drivers", "temp", "tmp"
        };

        public static List<Soft> Enumerate(List<string> roots, bool wholeDisk,
                                           Action<string> status, Func<bool> stop, out int errors)
        {
            errors = 0;
            List<Soft> list = new List<Soft>();
            Dictionary<string, Soft> map = new Dictionary<string, Soft>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Soft> pathMap = new Dictionary<string, Soft>(StringComparer.OrdinalIgnoreCase);

            Action<string, string, string> add = delegate(string name, string path, string from)
            {
                if (name == null) return;
                name = name.Trim();
                if (name.Length == 0) return;
                string pk = string.IsNullOrEmpty(path) ? null : path.TrimEnd('\\');
                Soft s = null;
                if (pk != null && pathMap.TryGetValue(pk, out s)) { }
                else if (map.TryGetValue(name, out s)) { }
                else
                {
                    // 按安装目录叶子名（或父目录叶子名）回挂：AppData 数据目录、App Paths
                    // 子目录常与安装目录同名，合并到已有软件，显示名沿用更权威的中文名。
                    string leaf = pk == null ? null : Path.GetFileName(pk);
                    string parentLeaf = leaf;
                    if (pk != null)
                    {
                        string par = Path.GetDirectoryName(pk);
                        if (par != null) parentLeaf = Path.GetFileName(par);
                    }
                    if (leaf != null)
                    {
                        foreach (KeyValuePair<string, Soft> kv in map)
                        {
                            foreach (string p in kv.Value.Paths)
                            {
                                if (p == null) continue;
                                string pl = Path.GetFileName(p.TrimEnd('\\'));
                                string pp = Path.GetDirectoryName(p);
                                string ppl = pp == null ? "" : Path.GetFileName(pp);
                                if (string.Equals(pl, leaf, StringComparison.OrdinalIgnoreCase))
                                { s = kv.Value; break; }
                            }
                            if (s != null) break;
                        }
                    }
                }
                if (s == null)
                {
                    s = new Soft();
                    s.Name = name;
                    s.From = from;
                    list.Add(s);
                }
                else if (NameScore(name, from) > NameScore(s.Name, s.From))
                {
                    // 新名字更好（含中文 / 来源更权威）就替换显示名
                    s.Name = name;
                    s.From = from;
                }
                s.AddPath(path);
                map[name] = s;
                if (pk != null) pathMap[pk] = s;
            };

            // 1) 注册表已安装程序 + App Paths
            if (status != null) status("正在读取已安装程序列表…");
            CollectRegistry(add);
            CollectAppPaths(add);

            // 2) 开始菜单快捷方式（补充便携版/绿色软件）
            if (status != null) status("正在读取开始菜单快捷方式…");
            CollectStartMenu(add);

            // 2) 指定的数据/程序目录
            foreach (string root in roots)
            {
                if (stop != null && stop()) break;
                if (!Directory.Exists(root)) { errors++; continue; }
                if (status != null) status("正在枚举：" + root);
                string[] subs;
                try { subs = Directory.GetDirectories(root); }
                catch { errors++; continue; }
                string tag = Path.GetFileName(root.TrimEnd('\\'));
                foreach (string sub in subs)
                {
                    if (stop != null && stop()) break;
                    if (!IsUsableDir(sub)) continue;
                    string nm = Path.GetFileName(sub);
                    add(nm, sub, tag);
                    // Packages / Programs 这类聚合目录，把里面的每个应用单独列出
                    if (string.Equals(nm, "Packages", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(nm, "Programs", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(nm, "WindowsApps", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] inner;
                        try { inner = Directory.GetDirectories(sub); }
                        catch { continue; }
                        foreach (string s2 in inner)
                        {
                            if (stop != null && stop()) break;
                            if (!IsUsableDir(s2)) continue;
                            string nm2 = Path.GetFileName(s2);
                            if (nm2.Length == 0 || nm2.StartsWith("$")) continue;
                            add(nm2, s2, nm);
                        }
                    }
                }
            }

            // 3) 全盘：所有固定磁盘的一级目录（及常见软件目录下的二级目录）
            if (wholeDisk)
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (stop != null && stop()) break;
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    if (status != null) status("正在枚举磁盘：" + d.Name);
                    string[] l1;
                    try { l1 = Directory.GetDirectories(d.RootDirectory.FullName); }
                    catch { errors++; continue; }
                    foreach (string dir1 in l1)
                    {
                        if (stop != null && stop()) break;
                        if (!IsUsableDir(dir1)) continue;
                        string n1 = Path.GetFileName(dir1);
                        if (SkipNames.Contains(n1)) continue;
                        add(n1, dir1, d.Name.TrimEnd('\\'));
                        // 常见软件集合目录下再展开一层，让里面的每个软件单独出现
                        string[] l2;
                        try { l2 = Directory.GetDirectories(dir1); }
                        catch { continue; }
                        if (l2.Length > 60) continue;   // 太大的一级目录不展开，避免噪音
                        foreach (string dir2 in l2)
                        {
                            if (stop != null && stop()) break;
                            if (!IsUsableDir(dir2)) continue;
                            string n2 = Path.GetFileName(dir2);
                            if (SkipNames.Contains(n2)) continue;
                            add(n2, dir2, n1);
                        }
                    }
                }
            }

            return FoldChildDirs(MergeSamePath(list));
        }

        static bool IsRealAppSource(string from)
        {
            return from == "注册表" || from == "开始菜单" || from == "App Paths";
        }

        // 安装目录的子目录（locales / resources / versions 等）并入父软件，不单独列出
        static List<Soft> FoldChildDirs(List<Soft> list)
        {
            Dictionary<string, Soft> byPath = new Dictionary<string, Soft>(StringComparer.OrdinalIgnoreCase);
            foreach (Soft s in list)
                foreach (string p in s.Paths)
                    if (p != null) byPath[p.TrimEnd('\\')] = s;

            List<Soft> keep = new List<Soft>();
            foreach (Soft s in list)
            {
                if (IsRealAppSource(s.From)) { keep.Add(s); continue; } // 注册过的真实应用永不被折叠吞并
                Soft parent = null;
                foreach (string p in s.Paths)
                {
                    if (p == null) continue;
                    string par = Path.GetDirectoryName(p);
                    if (par == null) continue;
                    string key = par.TrimEnd('\\');
                    Soft ps;
                    if (!IsGenericRoot(key) && byPath.TryGetValue(key, out ps) && ps != s && IsRealAppSource(ps.From)) { parent = ps; break; }
                }
                if (parent != null)
                {
                    foreach (string p in s.Paths) parent.AddPath(p);
                    continue;
                }
                keep.Add(s);
            }
            return keep;
        }

        static bool IsGenericRoot(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            int colon = key.IndexOf(':');
            if (colon >= 0 && key.Length <= colon + 2) return true; // 盘符根目录
            string lower = key.ToLowerInvariant();
            return lower.EndsWith("\\program files") || lower.EndsWith("\\program files (x86)") ||
                   lower.EndsWith("\\programdata") || lower.EndsWith("\\windows") ||
                   lower.EndsWith("\\users") || lower.EndsWith("\\windowsapps") ||
                   lower.EndsWith("\\packages") || lower.EndsWith("\\programs");
        }

        // 名字评分：优先中文，其次注册表/快捷方式来源，名字过长扣分
        static int NameScore(string n, string from)
        {
            int sc = 0;
            bool cjk = false;
            foreach (char c in n) { if (c >= 0x4E00 && c <= 0x9FFF) { cjk = true; break; } }
            if (cjk) sc += 100;
            if (from == "注册表") sc += 30;
            else if (from == "开始菜单") sc += 15;
            if (n.Length >= 2 && n.Length <= 18) sc += 5;
            sc -= Math.Max(0, n.Length - 20);
            return sc;
        }

        // 同路径条目合并成一个软件，避免重复（如“7-Zip”与“7-Zip 24.09 (x64)”）
        static List<Soft> MergeSamePath(List<Soft> list)
        {
            Dictionary<string, Soft> byPath = new Dictionary<string, Soft>(StringComparer.OrdinalIgnoreCase);
            List<Soft> outp = new List<Soft>();
            foreach (Soft s in list)
            {
                Soft keep = null;
                foreach (string p in s.Paths)
                {
                    string k = p == null ? null : p.TrimEnd('\\');
                    if (k != null && byPath.TryGetValue(k, out keep)) break;
                }
                if (keep == null)
                {
                    outp.Add(s);
                    foreach (string p in s.Paths)
                    {
                        string k = p == null ? null : p.TrimEnd('\\');
                        if (k != null) byPath[k] = s;
                    }
                    continue;
                }
                foreach (string p in s.Paths) keep.AddPath(p);
                foreach (Rec r in s.Files) keep.Files.Add(r);
                keep.TotalSize += s.TotalSize;
                if (NameScore(s.Name, s.From) > NameScore(keep.Name, keep.From))
                {
                    keep.Name = s.Name;
                    keep.From = s.From;
                }
            }
            return outp;
        }

        static string StripShortcutSuffix(string n)
        {
            if (n == null) return "";
            n = n.Trim();
            string[] suf = new string[] { " - 快捷方式", "- 快捷方式", "（快捷方式）", "(快捷方式)" };
            foreach (string s in suf)
                if (n.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - s.Length).Trim();
            return n;
        }

        static bool IsNoiseName(string n)
        {
            string low = n.ToLower();
            string[] noise = new string[] { "卸载", "删除", "移除", "关于", "帮助", "手册", "手动",
                "语言首选项", "隐私浏览", "readme", "help", "manual", "uninstall", "uninstaller",
                "remove", "unins", "repair" };
            foreach (string w in noise) if (low.Contains(w)) return true;
            return false;
        }

        static void CollectAppPaths(Action<string, string, string> add)
        {
            string[] subKeys = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths" };
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            foreach (RegistryKey hive in hives)
            {
                foreach (string sk in subKeys)
                {
                    RegistryKey k = null;
                    try { k = hive.OpenSubKey(sk); } catch { k = null; }
                    if (k == null) continue;
                    string[] names = null;
                    try { names = k.GetSubKeyNames(); } catch { }
                    if (names != null)
                    {
                        foreach (string n in names)
                        {
                            RegistryKey app = null;
                            try { app = k.OpenSubKey(n); } catch { }
                            if (app == null) continue;
                            try
                            {
                                string target = app.GetValue("") as string;
                                if (string.IsNullOrEmpty(target)) continue;
                                target = Environment.ExpandEnvironmentVariables(target).Trim().Trim('"');
                                if (!File.Exists(target)) continue;
                                string tlower = target.ToLower();
                                if (tlower.Contains("\\windows\\") || tlower.Contains("\\system32\\")) continue;
                                string dir = Path.GetDirectoryName(target);
                                string leaf = Path.GetFileNameWithoutExtension(target);
                                if (dir == null || leaf == null || leaf.Length < 2) continue;
                                if (IsNoiseName(leaf)) continue;
                                add(leaf, dir, "App Paths");
                            }
                            catch { }
                            finally { app.Close(); }
                        }
                    }
                    k.Close();
                }
            }
        }

        static void CollectStartMenu(Action<string, string, string> add)
        {
            List<string> dirs = new List<string>();
            try
            {
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"));
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
            }
            catch { return; }

            foreach (string d in dirs)
            {
                if (!Directory.Exists(d)) continue;
                List<string> lnks = new List<string>();
                try { CollectLnk(d, lnks, 0); } catch { }
                foreach (string f in lnks)
                {
                    try
                    {
                        string target = ResolveLnk(f);
                        if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                        string lower = target.ToLower();
                        if (lower.Contains("\\windows\\") || lower.Contains("uninstall") ||
                            lower.Contains("\\temp\\") || lower.Contains("readme")) continue;
                        string tLeaf = Path.GetFileNameWithoutExtension(target).ToLower();
                        if (tLeaf == "setup" || tLeaf == "uninstall" || tLeaf == "unins000" ||
                            tLeaf == "uninstaller" || tLeaf == "remove" || tLeaf == "repair") continue;
                        string dir = Path.GetDirectoryName(target);
                        if (string.IsNullOrEmpty(dir) || !IsUsableDir(dir)) continue;
                        string nm = Path.GetFileNameWithoutExtension(f);
                        nm = StripShortcutSuffix(nm);
                        if (nm.Length < 2 || IsNoiseName(nm)) continue;
                        add(nm, dir, "开始菜单");
                    }
                    catch { }
                }
            }
        }

        static void CollectLnk(string dir, List<string> outp, int depth)
        {
            if (depth > 3) return;
            string[] fs;
            try { fs = Directory.GetFiles(dir); } catch { return; }
            foreach (string f in fs)
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) outp.Add(f);
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { return; }
            foreach (string s in subs) CollectLnk(s, outp, depth + 1);
        }

        static string ResolveLnk(string path)
        {
            object shell = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (lnk == null) return null;
                object tp = lnk.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null);
                string s = tp as string;
                if (string.IsNullOrEmpty(s)) return null;
                return Environment.ExpandEnvironmentVariables(s);
            }
            catch { return null; }
            finally
            {
                try { if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
                catch { }
            }
        }

        static void CollectRegistry(Action<string, string, string> add)
        {
            string[] subKeys = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };

            foreach (RegistryKey hive in hives)
            {
                foreach (string sk in subKeys)
                {
                    RegistryKey k = null;
                    try { k = hive.OpenSubKey(sk); }
                    catch { k = null; }
                    if (k == null) continue;
                    string[] names;
                    try { names = k.GetSubKeyNames(); }
                    catch { names = new string[0]; }
                    foreach (string n in names)
                    {
                        RegistryKey app = null;
                        try { app = k.OpenSubKey(n); }
                        catch { app = null; }
                        if (app == null) continue;
                        try
                        {
                            object sc = app.GetValue("SystemComponent");
                            if (sc != null && sc.ToString() == "1") continue;
                            string dn = app.GetValue("DisplayName") as string;
                            if (dn == null || dn.Trim().Length == 0) continue;
                            string path = ResolveInstallPath(app);
                            add(dn, path, "注册表");
                        }
                        catch { }
                        finally { app.Close(); }
                    }
                    k.Close();
                }
            }
        }

        static string ResolveInstallPath(RegistryKey app)
        {
            string loc = app.GetValue("InstallLocation") as string;
            loc = CleanPath(loc);
            if (loc != null && Directory.Exists(loc)) return loc;

            string icon = app.GetValue("DisplayIcon") as string;
            string exe = ExtractPath(icon);
            if (exe != null && File.Exists(exe))
            {
                string dir = Path.GetDirectoryName(exe);
                if (dir != null && Directory.Exists(dir)) return GuessRoot(dir);
            }
            string un = app.GetValue("UninstallString") as string;
            exe = ExtractPath(un);
            if (exe != null && File.Exists(exe))
            {
                string dir = Path.GetDirectoryName(exe);
                if (dir != null && Directory.Exists(dir)) return GuessRoot(dir);
            }
            return null;
        }

        static string CleanPath(string p)
        {
            if (p == null) return null;
            p = Environment.ExpandEnvironmentVariables(p).Trim().Trim('"');
            if (p.Length == 0) return null;
            if (File.Exists(p)) p = Path.GetDirectoryName(p);
            return p.TrimEnd('\\');
        }

        static string ExtractPath(string s)
        {
            if (s == null) return null;
            s = Environment.ExpandEnvironmentVariables(s).Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("\""))
            {
                int e = s.IndexOf('"', 1);
                if (e > 1) return s.Substring(1, e - 1);
                return null;
            }
            // 形如 C:\xx\uninst.exe /S 或 C:\xx\app.exe,0
            int cut = s.IndexOf(",0");
            if (cut > 0) s = s.Substring(0, cut);
            int sp = s.IndexOf(" /");
            if (sp > 0) s = s.Substring(0, sp);
            int dash = s.IndexOf(" -");
            if (dash > 0) s = s.Substring(0, dash);
            s = s.Trim().Trim('"');
            if (s.Length == 0) return null;
            return s;
        }

        static string GuessRoot(string dir)
        {
            string name = Path.GetFileName(dir).ToLowerInvariant();
            if (name.Contains("uninstall") || name.Contains("install") ||
                name == "bin" || name == "app" || name == "application" || name == "x64" ||
                name == "x86" || name == "current")
            {
                string parent = Path.GetDirectoryName(dir);
                if (parent != null && Directory.Exists(parent)) return parent;
            }
            return dir;
        }

        static bool IsUsableDir(string dir)
        {
            try
            {
                FileAttributes a = File.GetAttributes(dir);
                if ((a & FileAttributes.ReparsePoint) != 0) return false;
                if ((a & FileAttributes.Hidden) != 0) return false;
                if ((a & FileAttributes.System) != 0) return false;
                return true;
            }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- 单软件扫描
    static class FileScanner
    {
        public static void Scan(Soft soft, int maxFiles, Func<bool> shouldStop)
        {
            foreach (string root in soft.Paths)
            {
                if (shouldStop != null && shouldStop()) return;
                if (!Directory.Exists(root)) continue;
                int baseLen = root.TrimEnd('\\').Length + 1;
                Traverse(root, soft, baseLen, maxFiles, shouldStop);
                if (soft.Truncated) return;
            }
        }

        // 剔除磁盘上已不存在的记录（外部删除文件后刷新用），返回剔除数量
        public static int PurgeMissing(Soft soft)
        {
            int n = 0;
            lock (soft.Files)
            {
                n = soft.Files.RemoveAll(delegate(Rec r) { return !File.Exists(r.Full); });
                long sum = 0;
                foreach (Rec r in soft.Files) sum += r.SizeBytes;
                soft.TotalSize = sum;
            }
            return n;
        }

        static void Traverse(string start, Soft soft, int baseLen, int maxFiles, Func<bool> shouldStop)
        {
            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(start, 0));
            List<Rec> batch = new List<Rec>(400);
            int total = soft.Files.Count;

            while (stack.Count > 0)
            {
                if (shouldStop != null && shouldStop()) { Flush(soft, batch); return; }
                KeyValuePair<string, int> cur = stack.Pop();
                string dir = cur.Key;

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = new string[0]; }

                foreach (string f in files)
                {
                    if (total + batch.Count >= maxFiles) { soft.Truncated = true; break; }
                    Rec r = Make(f, soft.Name, baseLen);
                    if (r != null) batch.Add(r);
                }
                if (batch.Count >= 300) Flush(soft, batch);
                if (soft.Truncated) break;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { subs = new string[0]; }
                foreach (string s in subs)
                {
                    try
                    {
                        FileAttributes a = File.GetAttributes(s);
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(new KeyValuePair<string, int>(s, cur.Value + 1));
                }
            }
            Flush(soft, batch);
        }

        static void Flush(Soft soft, List<Rec> batch)
        {
            if (batch.Count == 0) return;
            lock (soft.Files)
            {
                long add = 0;
                foreach (Rec r in batch) add += r.SizeBytes;
                soft.Files.AddRange(batch);
                soft.TotalSize += add;
            }
            batch.Clear();
        }

        static Rec Make(string full, string softName, int baseLen)
        {
            try
            {
                FileInfo fi = new FileInfo(full);
                if (!fi.Exists) return null;
                Rec r = new Rec();
                r.Soft = softName;
                r.Name = fi.Name;
                r.Full = full;
                r.Rel = full.Length > baseLen ? full.Substring(baseLen) : fi.Name;
                r.Ext = Path.GetExtension(full).ToLowerInvariant();
                r.SizeBytes = fi.Length;
                r.MTime = fi.LastWriteTime;
                r.Category = Rules.Category(r.Rel);
                r.Risk = Rules.Risk(r.Rel, r.Category, r.Ext);
                return r;
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------------- 归档/还原/删除
    class ManifestEntry
    {
        public string OrigPath;
        public string ArchivePath;
        public string Group;
        public string Name;
        public string Category;
        public string Time;
    }

    static class Archiver
    {
        public static string Root(string exeDir) { return Path.Combine(exeDir, "归档隔离区"); }
        public static string ManifestFile(string exeDir) { return Path.Combine(Root(exeDir), "manifest.tsv"); }

        public static List<ManifestEntry> Load(string exeDir)
        {
            List<ManifestEntry> list = new List<ManifestEntry>();
            string mf = ManifestFile(exeDir);
            if (!File.Exists(mf)) return list;
            try
            {
                string[] lines = File.ReadAllLines(mf, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (line.Trim().Length == 0) continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 6) continue;
                    ManifestEntry e = new ManifestEntry();
                    e.OrigPath = p[0];
                    e.ArchivePath = p[1];
                    e.Group = p[2];
                    e.Name = p[3];
                    e.Category = p[4];
                    e.Time = p[5];
                    list.Add(e);
                }
            }
            catch { }
            return list;
        }

        public static void Save(string exeDir, List<ManifestEntry> list)
        {
            try
            {
                Directory.CreateDirectory(Root(exeDir));
                StringBuilder sb = new StringBuilder();
                foreach (ManifestEntry e in list)
                    sb.AppendLine(string.Join("\t", new string[] {
                        Esc(e.OrigPath), Esc(e.ArchivePath), Esc(e.Group),
                        Esc(e.Name), Esc(e.Category), Esc(e.Time) }));
                File.WriteAllText(ManifestFile(exeDir), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        public static int Archive(List<Rec> recs, string exeDir, List<string> failed)
        {
            List<ManifestEntry> man = Load(exeDir);
            int ok = 0;
            foreach (Rec r in recs)
            {
                try
                {
                    if (!File.Exists(r.Full)) { failed.Add(r.Name + "（已不存在）"); continue; }
                    string dest = Path.Combine(Root(exeDir), Safe(r.Soft), r.Rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) dest = UniquePath(dest);
                    MoveFile(r.Full, dest);

                    ManifestEntry e = new ManifestEntry();
                    e.OrigPath = r.Full;
                    e.ArchivePath = dest;
                    e.Group = r.Soft;
                    e.Name = r.Name;
                    e.Category = r.Category;
                    e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    man.Add(e);

                    r.Full = dest;
                    ok++;
                }
                catch (Exception ex) { failed.Add(r.Name + "（" + ex.Message + "）"); }
            }
            Save(exeDir, man);
            return ok;
        }

        public static int Restore(string exeDir, List<ManifestEntry> chosen, List<string> failed)
        {
            List<ManifestEntry> man = Load(exeDir);
            int ok = 0;
            foreach (ManifestEntry c in chosen)
            {
                try
                {
                    if (!File.Exists(c.ArchivePath)) { failed.Add(c.Name + "（隔离区文件已丢失）"); continue; }
                    if (File.Exists(c.OrigPath)) { failed.Add(c.Name + "（原位置已有同名文件）"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(c.OrigPath));
                    MoveFile(c.ArchivePath, c.OrigPath);
                    man.RemoveAll(delegate(ManifestEntry x) { return x.ArchivePath == c.ArchivePath; });
                    ok++;
                }
                catch (Exception ex) { failed.Add(c.Name + "（" + ex.Message + "）"); }
            }
            Save(exeDir, man);
            return ok;
        }

        public static int Delete(List<Rec> recs, bool force, List<string> failed, List<string> kept)
        {
            int ok = 0;
            foreach (Rec r in recs)
            {
                if (!force && r.Risk == Rules.PROTECTED) { kept.Add(r.Name); continue; }
                try
                {
                    if (File.Exists(r.Full)) File.Delete(r.Full);
                    ok++;
                }
                catch (Exception ex) { failed.Add(r.Name + "（" + ex.Message + "）"); }
            }
            return ok;
        }

        static string UniquePath(string p)
        {
            string dir = Path.GetDirectoryName(p);
            string name = Path.GetFileNameWithoutExtension(p);
            string ext = Path.GetExtension(p);
            for (int i = 1; i < 1000; i++)
            {
                string q = Path.Combine(dir, name + "_" + i + ext);
                if (!File.Exists(q)) return q;
            }
            return p;
        }

        static void MoveFile(string from, string to)
        {
            try { File.Move(from, to); }
            catch (IOException)
            {
                File.Copy(from, to, false);
                File.Delete(from);
            }
        }

        static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length == 0 ? "_" : name;
        }
    }

    // ---------------------------------------------------------------- 风险确认
    class ConfirmForm : Form
    {
        public bool ForceAll = false;

        public ConfirmForm(List<Rec> prot)
        {
            Text = "风险确认：包含「删除影响使用」的文件";
            Width = 660;
            Height = 470;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label warn = new Label();
            warn.Text = "以下 " + prot.Count + " 个文件被标记为「删除影响使用」，删除后可能导致软件无法运行、配置或存档丢失：";
            warn.ForeColor = Rules.RiskColor(Rules.PROTECTED);
            warn.Dock = DockStyle.Top;
            warn.Height = 39;
            warn.Padding = new Padding(12, 10, 12, 0);

            ListView lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.Dock = DockStyle.Fill;
            lv.Columns.Add("文件", 300);
            lv.Columns.Add("分类", 90);
            lv.Columns.Add("大小", 80);
            lv.Columns.Add("所在目录", 160);
            Controls.Add(lv);
            foreach (Rec r in prot)
            {
                ListViewItem it = new ListViewItem(r.Name);
                it.SubItems.Add(r.Category);
                it.SubItems.Add(Rules.SizeText(r.SizeBytes));
                it.SubItems.Add(Path.GetDirectoryName(r.Rel));
                it.ForeColor = Rules.RiskColor(Rules.PROTECTED);
                lv.Items.Add(it);
                if (lv.Items.Count > 2000) break;
            }

            Panel bottomBar = new Panel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.Height = 92;
            Controls.Add(bottomBar);
            Controls.Add(warn);

            CheckBox chk = new CheckBox();
            chk.Text = "我已知晓风险，仍然彻底删除这些受保护文件（不可还原）";
            chk.ForeColor = Rules.RiskColor(Rules.PROTECTED);
            chk.Location = new Point(12, 10);
            chk.AutoSize = true;
            bottomBar.Controls.Add(chk);

            Button b1 = new Button();
            b1.Text = "仅删除可清理项";
            b1.Location = new Point(150, 44);
            b1.Width = 140;
            b1.Click += delegate { ForceAll = false; DialogResult = DialogResult.OK; Close(); };
            bottomBar.Controls.Add(b1);

            Button b2 = new Button();
            b2.Text = "全部彻底删除";
            b2.Location = new Point(300, 44);
            b2.Width = 140;
            b2.ForeColor = Rules.RiskColor(Rules.PROTECTED);
            b2.Click += delegate
            {
                if (!chk.Checked)
                {
                    MessageBox.Show(this, "请先勾选上方风险确认，才能连同受保护文件一起删除。", "提示");
                    return;
                }
                ForceAll = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            bottomBar.Controls.Add(b2);

            Button b3 = new Button();
            b3.Text = "取消";
            b3.Location = new Point(450, 44);
            b3.Width = 90;
            b3.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bottomBar.Controls.Add(b3);
        }
    }

    // ---------------------------------------------------------------- 还原
    class RestoreForm : Form
    {
        public List<ManifestEntry> Chosen = new List<ManifestEntry>();
        ListView lv;

        public RestoreForm(string exeDir)
        {
            Text = "还原已归档文件";
            Width = 940;
            Height = 500;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9F);

            lv = new ListView();
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.Dock = DockStyle.Fill;
            lv.Columns.Add("名称", 220);
            lv.Columns.Add("软件", 130);
            lv.Columns.Add("分类", 90);
            lv.Columns.Add("归档时间", 140);
            lv.Columns.Add("原始路径", 340);
            Controls.Add(lv);
            foreach (ManifestEntry e in Archiver.Load(exeDir))
            {
                ListViewItem it = new ListViewItem(e.Name);
                it.SubItems.Add(e.Group);
                it.SubItems.Add(e.Category);
                it.SubItems.Add(e.Time);
                it.SubItems.Add(e.OrigPath);
                it.Tag = e;
                lv.Items.Add(it);
            }

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 46;
            Controls.Add(bar);

            Button all = new Button();
            all.Text = "全选";
            all.Location = new Point(10, 10);
            all.Width = 74;
            all.Click += delegate { foreach (ListViewItem i in lv.Items) i.Checked = true; };
            bar.Controls.Add(all);

            Button none = new Button();
            none.Text = "取消勾选";
            none.Location = new Point(90, 10);
            none.Width = 88;
            none.Click += delegate { foreach (ListViewItem i in lv.Items) i.Checked = false; };
            bar.Controls.Add(none);

            Button ok = new Button();
            ok.Text = "还原所选";
            ok.Location = new Point(720, 10);
            ok.Width = 96;
            ok.Click += delegate
            {
                Chosen.Clear();
                foreach (ListViewItem i in lv.Items)
                    if (i.Checked) Chosen.Add((ManifestEntry)i.Tag);
                if (Chosen.Count == 0)
                {
                    MessageBox.Show(this, "请先勾选要还原的记录。", "提示");
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };
            bar.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "关闭";
            cancel.Location = new Point(824, 10);
            cancel.Width = 96;
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bar.Controls.Add(cancel);

            Label tip = new Label();
            tip.Text = "已归档：" + lv.Items.Count + " 项";
            tip.Location = new Point(190, 14);
            tip.AutoSize = true;
            bar.Controls.Add(tip);
        }
    }

    // ---------------------------------------------------------------- 主窗体
    public class MainForm : Form
    {
        const int MAX_FILES_PER_SOFT = int.MaxValue;   // 不设文件数上限

        string appDir;
        List<string> roots = new List<string>();
        bool wholeDisk = true;

        List<Soft> softs = new List<Soft>();
        List<Soft> softView = new List<Soft>();
        Button btnRefresh;
        bool refreshing;
        bool barBusy;
        Soft currentSoft;
        volatile Soft scanTarget;

        List<Rec> viewRecs = new List<Rec>();
        HashSet<Rec> checkedRecs = new HashSet<Rec>();
        long viewSize;
        int viewProtected;

        Thread enumThread;
        volatile bool stopEnum = false;

        System.Windows.Forms.Timer tickTimer, searchTimer, clickTimer;
        int tickCount = 0;
        int pendingClick = -1;
        int softSortCol = 0;
        bool softSortAsc = true;

        Label lblRoots, lblBar;
        CheckBox chkWhole;
        Button btnEnum, btnAddRoot;
        ProgressBar progress;
        TextBox txtGroup, txtSearch;
        ComboBox cmbSort, cmbView, cmbCat;
        CheckBox chkDesc;
        ListView lvSofts, lvFiles;
        FlowLayoutPanel flowFilter;
        ToolStripStatusLabel lblStatus;
        string lastBarSig = "";

        public MainForm()
        {
            Text = "脏数据本地归档工具 v2.0";
            Width = 1360;
            Height = 820;
            MinimumSize = new Size(1080, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            KeyPreview = true;
            appDir = Path.GetDirectoryName(Application.ExecutablePath);

            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5) { e.Handled = true; RefreshCurrentSoft(); }
            };

            clickTimer = new System.Windows.Forms.Timer();
            clickTimer.Interval = 250;
            clickTimer.Tick += delegate
            {
                clickTimer.Stop();
                int idx = pendingClick;
                pendingClick = -1;
                if (idx < 0 || idx >= viewRecs.Count) return;
                Rec r = viewRecs[idx];
                if (checkedRecs.Contains(r)) checkedRecs.Remove(r);
                else checkedRecs.Add(r);
                lvFiles.RedrawItems(idx, idx, false);
                UpdateStatus();
            };

            BuildUi();
            LoadDefaultRoots();

            searchTimer = new System.Windows.Forms.Timer();
            searchTimer.Interval = 320;
            searchTimer.Tick += delegate { searchTimer.Stop(); RebuildView(true); };

            tickTimer = new System.Windows.Forms.Timer();
            tickTimer.Interval = 700;
            tickTimer.Tick += delegate { OnTick(); };
            tickTimer.Start();

            Shown += delegate
            {
                try
                {
                    splitBox.Panel1MinSize = 200;
                    splitBox.Panel2MinSize = 460;
                    splitBox.SplitterDistance = 400;
                }
                catch { }
                StartEnum();
            };
            FormClosing += delegate { stopEnum = true; scanTarget = null; };
        }

        SplitContainer splitBox;

        // ------------------------------------------------ UI
        void BuildUi()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 96;
            top.BackColor = Color.FromArgb(246, 247, 251);
            top.Padding = new Padding(10, 8, 10, 4);

            FlowLayoutPanel f1 = new FlowLayoutPanel();
            f1.Dock = DockStyle.Top;
            f1.AutoSize = true;
            f1.WrapContents = true;

            f1.Controls.Add(MkLabel("扫描范围："));
            lblRoots = new Label();
            lblRoots.AutoSize = false;
            lblRoots.Width = 300;
            lblRoots.Height = 24;
            lblRoots.TextAlign = ContentAlignment.MiddleLeft;
            lblRoots.ForeColor = Color.FromArgb(60, 70, 90);
            f1.Controls.Add(lblRoots);

            chkWhole = new CheckBox();
            chkWhole.Text = "全盘";
            chkWhole.AutoSize = true;
            chkWhole.Checked = true;
            chkWhole.Margin = new Padding(6, 6, 6, 0);
            chkWhole.CheckedChanged += delegate { wholeDisk = chkWhole.Checked; StartEnum(); };
            f1.Controls.Add(chkWhole);

            btnAddRoot = MkBtn("添加目录…", delegate { AddRoot(); });
            f1.Controls.Add(btnAddRoot);
            btnEnum = MkBtn("重新枚举软件", delegate { StartEnum(); });
            btnEnum.Width = 110;
            f1.Controls.Add(btnEnum);

            progress = new ProgressBar();
            progress.Width = 150;
            progress.Height = 16;
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 0;
            progress.Visible = false;
            progress.Margin = new Padding(8, 6, 0, 0);
            f1.Controls.Add(progress);

            FlowLayoutPanel f2 = new FlowLayoutPanel();
            f2.Dock = DockStyle.Top;
            f2.AutoSize = true;
            f2.WrapContents = true;

            f2.Controls.Add(MkLabel("软件筛选："));
            txtGroup = new TextBox();
            txtGroup.Width = 130;
            txtGroup.TextChanged += delegate { RefreshSofts(); };
            f2.Controls.Add(txtGroup);

            f2.Controls.Add(MkLabel("排序："));
            cmbSort = new ComboBox();
            cmbSort.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSort.Width = 100;
            cmbSort.Items.AddRange(new object[] { "名称", "大小", "修改时间", "分类", "类型", "风险" });
            cmbSort.SelectedIndex = 0;
            cmbSort.SelectedIndexChanged += delegate { RebuildView(true); };
            f2.Controls.Add(cmbSort);

            chkDesc = new CheckBox();
            chkDesc.Text = "降序";
            chkDesc.AutoSize = true;
            chkDesc.Margin = new Padding(6, 6, 10, 0);
            chkDesc.CheckedChanged += delegate { RebuildView(true); };
            f2.Controls.Add(chkDesc);

            f2.Controls.Add(MkLabel("视图："));
            cmbView = new ComboBox();
            cmbView.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbView.Width = 120;
            cmbView.Items.AddRange(new object[] { "全部文件", "按数据分类", "按文件类型" });
            cmbView.SelectedIndex = 0;
            cmbView.SelectedIndexChanged += delegate { OnViewChange(); };
            f2.Controls.Add(cmbView);

            f2.Controls.Add(MkLabel("筛选："));
            cmbCat = new ComboBox();
            cmbCat.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCat.Width = 140;
            cmbCat.Enabled = false;
            cmbCat.SelectedIndexChanged += delegate { RebuildView(true); };
            f2.Controls.Add(cmbCat);

            f2.Controls.Add(MkLabel("搜索："));
            txtSearch = new TextBox();
            txtSearch.Width = 160;
            txtSearch.TextChanged += delegate
            {
                searchTimer.Stop();
                searchTimer.Start();
            };
            f2.Controls.Add(txtSearch);

            top.Controls.Add(f2);
            top.Controls.Add(f1);

            // 主体
            splitBox = new SplitContainer();
            splitBox.Dock = DockStyle.Fill;
            Controls.Add(splitBox);

            Panel leftBox = new Panel();
            leftBox.Dock = DockStyle.Fill;
            splitBox.Panel1.Controls.Add(leftBox);

            lvSofts = new ListView();
            lvSofts.Dock = DockStyle.Fill;
            lvSofts.View = View.Details;
            lvSofts.VirtualMode = true;
            lvSofts.FullRowSelect = true;
            lvSofts.HideSelection = false;
            lvSofts.MultiSelect = false;
            lvSofts.Columns.Add("软件", 146);
            lvSofts.Columns.Add("文件数", 66, HorizontalAlignment.Right);
            lvSofts.Columns.Add("占用", 72, HorizontalAlignment.Right);
            lvSofts.Columns.Add("位置", 140);
            lvSofts.RetrieveVirtualItem += delegate(object s, RetrieveVirtualItemEventArgs e)
            {
                e.Item = MakeSoftItem(e.ItemIndex);
            };
            lvSofts.SelectedIndexChanged += delegate { OnSoftSelected(); };
            lvSofts.ColumnClick += delegate(object s, ColumnClickEventArgs e)
            {
                if (e.Column == softSortCol) softSortAsc = !softSortAsc;
                else { softSortCol = e.Column; softSortAsc = true; }
                RefreshSofts();
            };
            lvSofts.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) OpenSoftFolder();
            };

            ContextMenuStrip softMenu = new ContextMenuStrip();
            softMenu.Items.Add("打开软件所在位置", null, delegate { OpenSoftFolder(); });
            softMenu.Items.Add("刷新该软件（重扫文件）", null, delegate { RefreshSelectedSoft(); });
            softMenu.Items.Add(new ToolStripSeparator());
            softMenu.Items.Add("重新枚举全部软件", null, delegate { StartEnum(); });
            softMenu.Items.Add("打开隔离区", null, delegate { OpenArchiveDir(); });
            lvSofts.ContextMenuStrip = softMenu;

            leftBox.Controls.Add(lvSofts);

            // 左侧顶部：标题 + 打开位置 / 刷新软件
            Panel softHead = new Panel();
            softHead.Dock = DockStyle.Top;
            softHead.Height = 36;
            softHead.BackColor = Color.FromArgb(246, 247, 251);

            Label l1 = new Label();
            l1.Text = "软件列表";
            l1.AutoSize = true;
            l1.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            l1.Margin = new Padding(4, 9, 10, 0);

            FlowLayoutPanel softBar = new FlowLayoutPanel();
            softBar.Dock = DockStyle.Fill;
            softBar.WrapContents = false;
            softBar.Padding = new Padding(2, 3, 2, 0);
            softBar.Controls.Add(l1);
            softBar.Controls.Add(MkBtn("打开位置", delegate { OpenSoftFolder(); }));
            softBar.Controls.Add(MkBtn("刷新软件", delegate { RefreshSelectedSoft(); }));
            softHead.Controls.Add(softBar);

            leftBox.Controls.Add(softHead);

            Panel rightBox = new Panel();
            rightBox.Dock = DockStyle.Fill;
            splitBox.Panel2.Controls.Add(rightBox);

            flowFilter = new FlowLayoutPanel();
            flowFilter.Dock = DockStyle.Top;
            flowFilter.Height = 46;
            flowFilter.AutoScroll = true;
            flowFilter.WrapContents = false;
            flowFilter.Padding = new Padding(4, 4, 4, 0);
            flowFilter.Resize += delegate
            {
                if (barBusy || cmbView == null || cmbView.SelectedIndex == 0) return;
                lastBarSig = "";
                BuildQuickBar(cmbView.SelectedIndex);
            };

            lvFiles = new ListView();
            lvFiles.Dock = DockStyle.Fill;
            lvFiles.View = View.Details;
            lvFiles.VirtualMode = true;
            lvFiles.FullRowSelect = true;
            lvFiles.HideSelection = false;
            lvFiles.MultiSelect = false;
            lvFiles.Columns.Add("选", 30);
            lvFiles.Columns.Add("名称", 200);
            lvFiles.Columns.Add("类型", 62);
            lvFiles.Columns.Add("分类", 74);
            lvFiles.Columns.Add("风险", 92);
            lvFiles.Columns.Add("大小", 70, HorizontalAlignment.Right);
            lvFiles.Columns.Add("修改时间", 128);
            lvFiles.Columns.Add("相对路径", 190);
            lvFiles.RetrieveVirtualItem += delegate(object s, RetrieveVirtualItemEventArgs e)
            {
                e.Item = MakeFileItem(e.ItemIndex);
            };
            lvFiles.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks > 1) return;
                if (e.X > 36) return;          // 只在最左侧勾选列点击才切换勾选，避免误勾
                if (lvFiles.SelectedIndices.Count == 0) return;
                int idx = lvFiles.SelectedIndices[0];
                if (idx < 0 || idx >= viewRecs.Count) return;
                pendingClick = idx;
                clickTimer.Stop();
                clickTimer.Start();
            };
            lvFiles.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                clickTimer.Stop();
                if (lvFiles.SelectedIndices.Count == 0) return;
                int idx = lvFiles.SelectedIndices[0];
                if (idx >= 0 && idx < viewRecs.Count) OpenLocation(viewRecs[idx].Full);
            };
            rightBox.Controls.Add(lvFiles);
            rightBox.Controls.Add(flowFilter);
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("刷新文件列表（F5）", null, delegate { RefreshCurrentSoft(); });
            menu.Items.Add("打开文件所在位置", null, delegate { OpenSelectedLocation(); });
            menu.Items.Add("归档所选（可还原）", null, delegate { ArchiveSelected(); });
            menu.Items.Add("彻底删除所选", null, delegate { DeleteSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("全选当前视图", null, delegate { SetAllChecked(true); });
            menu.Items.Add("取消全部勾选", null, delegate { SetAllChecked(false); });
            lvFiles.ContextMenuStrip = menu;

            // 底部
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            bottom.BackColor = Color.FromArgb(246, 247, 251);
            bottom.Padding = new Padding(10, 6, 10, 6);
            Controls.Add(bottom);

            FlowLayoutPanel fb = new FlowLayoutPanel();
            fb.Dock = DockStyle.Fill;
            fb.WrapContents = false;
            bottom.Controls.Add(fb);

            btnRefresh = MkBtn("刷新文件列表", delegate { RefreshCurrentSoft(); });
            fb.Controls.Add(btnRefresh);
            fb.Controls.Add(MkBtn("全选当前", delegate { SetAllChecked(true); }));
            fb.Controls.Add(MkBtn("取消勾选", delegate { SetAllChecked(false); }));
            fb.Controls.Add(MkBtn("打开所在位置", delegate { OpenSelectedLocation(); }));
            fb.Controls.Add(MkBtn("归档所选（可还原）", delegate { ArchiveSelected(); }));
            fb.Controls.Add(MkBtn("还原已归档", delegate { RestoreArchived(); }));
            fb.Controls.Add(MkBtn("彻底删除所选", delegate { DeleteSelected(); }));
            fb.Controls.Add(MkBtn("打开隔离区", delegate { OpenArchiveDir(); }));

            lblBar = new Label();
            lblBar.AutoSize = false;
            lblBar.Width = 220;
            lblBar.Height = 24;
            lblBar.TextAlign = ContentAlignment.MiddleRight;
            lblBar.ForeColor = Color.FromArgb(110, 118, 132);
            fb.Controls.Add(lblBar);

            StatusStrip statusBar = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Text = "准备中…";
            statusBar.Items.Add(lblStatus);
            Controls.Add(statusBar);
            Controls.Add(top);
        }

        Label MkLabel(string t)
        {
            Label l = new Label();
            l.Text = t;
            l.AutoSize = true;
            l.Margin = new Padding(6, 8, 2, 0);
            return l;
        }

        Button MkBtn(string t, EventHandler h)
        {
            Button b = new Button();
            b.Text = t;
            b.AutoSize = true;
            b.Height = 26;
            b.Margin = new Padding(4, 4, 4, 0);
            b.FlatStyle = FlatStyle.System;
            b.Click += h;
            return b;
        }

        // ------------------------------------------------ 虚拟行
        ListViewItem MakeSoftItem(int i)
        {
            Soft s = softView[i];
            ListViewItem it = new ListViewItem(s.Name);
            it.SubItems.Add(s.State == 0 ? "-" : s.Files.Count.ToString("N0"));
            it.SubItems.Add(s.State == 0 ? "-" : Rules.SizeText(s.TotalSize));
            it.SubItems.Add(s.DisplayPath);
            if (s.State == 1) it.ForeColor = Color.FromArgb(90, 110, 160);
            return it;
        }

        ListViewItem MakeFileItem(int i)
        {
            Rec r = viewRecs[i];
            ListViewItem it = new ListViewItem(checkedRecs.Contains(r) ? "☑" : "☐");
            it.SubItems.Add(r.Name);
            it.SubItems.Add(Rules.ExtLabel(r.Ext));
            it.SubItems.Add(r.Category);
            it.SubItems.Add(Rules.RiskText(r.Risk));
            it.SubItems.Add(Rules.SizeText(r.SizeBytes));
            it.SubItems.Add(r.MTime.ToString("yyyy-MM-dd HH:mm"));
            it.SubItems.Add(r.Rel);
            it.ForeColor = Rules.RiskColor(r.Risk);
            return it;
        }

        // 预览用：等枚举完成后自动选中指定软件，并可切到指定视图
        public void PreviewSelect(string name, int viewIdx)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 900;
            t.Tick += delegate
            {
                if (softs.Count == 0) return;
                for (int i = 0; i < softView.Count; i++)
                {
                    if (softView[i].Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        lvSofts.SelectedIndices.Clear();
                        lvSofts.SelectedIndices.Add(i);
                        lvSofts.Focus();
                        t.Stop();
                        System.Windows.Forms.Timer t2 = new System.Windows.Forms.Timer();
                        t2.Interval = 500;
                        t2.Tick += delegate
                        {
                            if (viewRecs.Count == 0) return;
                            t2.Stop();
                            if (cmbView != null && viewIdx >= 0 && viewIdx < cmbView.Items.Count)
                                cmbView.SelectedIndex = viewIdx;
                        };
                        t2.Start();
                        return;
                    }
                }
            };
            t.Start();
        }

        // 界面自检：枚举→选软件→切三个视图，统计耗时
        public string RunUiTest()
        {
            StringBuilder sb = new StringBuilder();
            Stopwatch sw = Stopwatch.StartNew();
            while (softs.Count == 0 && sw.ElapsedMilliseconds < 30000)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            sb.AppendLine("枚举耗时=" + sw.ElapsedMilliseconds + "ms 软件数=" + softs.Count);
            if (softs.Count == 0) return sb.ToString();
            Application.DoEvents();
            Thread.Sleep(300);

            int firstFiles = 0;
            for (int i = 0; i < softView.Count && i < 40; i++)
            {
                lvSofts.SelectedIndices.Clear();
                lvSofts.SelectedIndices.Add(i);
                Application.DoEvents();
                Stopwatch w = Stopwatch.StartNew();
                while (viewRecs.Count == 0 && w.ElapsedMilliseconds < 3000)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }
                firstFiles = viewRecs.Count;
                if (firstFiles > 0) break;
            }
            sb.AppendLine("首个有文件软件行数=" + firstFiles);

            if (cmbView != null)
            {
                int[] seq = new int[] { 1, 2, 0 };
                foreach (int v in seq)
                {
                    Stopwatch w = Stopwatch.StartNew();
                    cmbView.SelectedIndex = v;
                    Application.DoEvents();
                    w.Stop();
                    sb.AppendLine("视图" + v + "耗时=" + w.ElapsedMilliseconds + "ms 行数=" + viewRecs.Count +
                                  " 快捷筛选=" + (flowFilter == null ? "-" : flowFilter.Controls.Count.ToString()));
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------ 枚举
        void LoadDefaultRoots()
        {
            roots.Clear();
            string[] defs = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string d in defs)
                if (d != null && d.Length > 0 && Directory.Exists(d) && !roots.Contains(d))
                    roots.Add(d);
            UpdateRootsLabel();
        }

        void AddRoot()
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择要加入扫描范围的目录（例如某个软件安装目录或存档目录）";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!roots.Contains(dlg.SelectedPath))
            {
                roots.Add(dlg.SelectedPath);
                UpdateRootsLabel();
                StartEnum();
            }
        }

        void UpdateRootsLabel()
        {
            if (lblRoots == null) return;
            string t = "默认位置 " + roots.Count + " 处";
            if (wholeDisk) t += " + 全部磁盘";
            lblRoots.Text = t;
            ToolTip tip = new ToolTip();
            tip.SetToolTip(lblRoots, string.Join("\r\n", roots.ToArray()));
        }

        void StartEnum()
        {
            if (enumThread != null && enumThread.IsAlive) { stopEnum = true; return; }
            stopEnum = false;
            UpdateRootsLabel();
            progress.Visible = true;
            progress.MarqueeAnimationSpeed = 30;
            btnEnum.Text = "停止枚举";
            lblStatus.Text = "正在收集本机软件列表…";

            List<string> rs = new List<string>(roots);
            bool wd = wholeDisk;
            enumThread = new Thread(delegate() { RunEnum(rs, wd); });
            enumThread.IsBackground = true;
            enumThread.Start();
        }

        void RunEnum(List<string> rs, bool wd)
        {
            int errors = 0;
            List<Soft> res = null;
            try
            {
                res = SoftFinder.Enumerate(rs, wd,
                    delegate(string msg) { this.BeginInvoke(new Action(delegate { lblStatus.Text = msg; })); },
                    delegate() { return stopEnum; },
                    out errors);
            }
            catch { }
            this.BeginInvoke(new Action(delegate
            {
                progress.Visible = false;
                progress.MarqueeAnimationSpeed = 0;
                btnEnum.Text = "重新枚举软件";
                if (res != null)
                {
                    softs = res;
                    currentSoft = null;
                    checkedRecs.Clear();
                    viewRecs = new List<Rec>();
                    lvFiles.VirtualListSize = 0;
                    RefreshSofts();
                    lblStatus.Text = "已发现 " + softs.Count + " 个软件（点选左侧任一软件即可扫描其文件）";
                    if (softView.Count > 0)
                    {
                        lvSofts.SelectedIndices.Clear();
                        lvSofts.SelectedIndices.Add(0);
                    }
                }
            }));
        }

        void RefreshSofts()
        {
            string kw = txtGroup.Text.Trim();
            softView = new List<Soft>();
            foreach (Soft s in softs)
            {
                if (kw.Length > 0 && s.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                softView.Add(s);
            }
            int col = softSortCol;
            bool asc = softSortAsc;
            softView.Sort(delegate(Soft a, Soft b)
            {
                int c;
                if (col == 1) c = a.Files.Count.CompareTo(b.Files.Count);
                else if (col == 2) c = a.TotalSize.CompareTo(b.TotalSize);
                else if (col == 3) c = string.Compare(a.PathText, b.PathText, StringComparison.OrdinalIgnoreCase);
                else c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return asc ? c : -c;
            });
            lvSofts.VirtualListSize = 0;
            lvSofts.VirtualListSize = softView.Count;
            lvSofts.Invalidate();
            lblBar.Text = softs.Count.ToString("N0") + " 个软件";
        }

        Soft SelectedSoft()
        {
            if (lvSofts.SelectedIndices.Count == 0) return null;
            int i = lvSofts.SelectedIndices[0];
            if (i < 0 || i >= softView.Count) return null;
            return softView[i];
        }

        void OpenSoftFolder()
        {
            Soft s = SelectedSoft();
            if (s == null)
            {
                MessageBox.Show(this, "请先在左侧选择一个软件。", "提示");
                return;
            }
            if (s.Paths.Count == 0 || !Directory.Exists(s.Paths[0]))
            {
                MessageBox.Show(this, "该软件只有注册表登记，没有可定位的安装目录。", "提示");
                return;
            }
            OpenFolder(s.Paths[0]);
        }

        void OpenFolder(string dir)
        {
            try { Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
        }

        // 刷新左侧选中的软件：没选中的先切过去，再整体重扫
        void RefreshSelectedSoft()
        {
            Soft s = SelectedSoft();
            if (s == null)
            {
                MessageBox.Show(this, "请先在左侧选择一个软件。", "提示");
                return;
            }
            if (s != currentSoft)
            {
                currentSoft = s;
                checkedRecs.Clear();
                RebuildChoices();
                RebuildView(true);
            }
            RefreshCurrentSoft();
        }

        void OnSoftSelected()
        {
            if (lvSofts.SelectedIndices.Count == 0) return;
            int i = lvSofts.SelectedIndices[0];
            if (i < 0 || i >= softView.Count) return;
            Soft s = softView[i];
            if (s == currentSoft) return;
            currentSoft = s;
            checkedRecs.Clear();
            RebuildChoices();
            StartSoftScan(s, false);
            RebuildView(true);
        }

        void RefreshCurrentSoft()
        {
            Soft s = currentSoft;
            if (s == null)
            {
                MessageBox.Show(this, "请先在左侧选择一个软件。", "提示");
                return;
            }
            if (refreshing || s.State == 1)
            {
                MessageBox.Show(this, "正在扫描，等扫完再刷新。", "提示");
                return;
            }
            refreshing = true;
            if (btnRefresh != null) btnRefresh.Enabled = false;
            lblStatus.Text = "正在刷新 " + s.Name + " 的文件列表…";
            Soft target = s;

            Thread t = new Thread(delegate()
            {
                // 一、先剔除磁盘上已经不存在的记录（用户手动删过文件的场景）
                int removed = 0;
                try { removed = FileScanner.PurgeMissing(target); }
                catch { }

                int goneCount = removed;
                this.BeginInvoke(new Action(delegate
                {
                    if (goneCount > 0) BumpView();
                }));

                // 二、完整重扫一份，补上新出现或大小/时间变了的文件，再整体替换
                Soft tmp = new Soft();
                tmp.Name = target.Name;
                foreach (string p in target.Paths) tmp.AddPath(p);
                try { FileScanner.Scan(tmp, MAX_FILES_PER_SOFT, delegate { return false; }); }
                catch { }

                this.BeginInvoke(new Action(delegate
                {
                    lock (target.Files)
                    {
                        target.Files = tmp.Files;
                        target.TotalSize = tmp.TotalSize;
                        target.Truncated = tmp.Truncated;
                    }
                    // 勾选状态按完整路径迁移，仍存在的文件保持勾选
                    List<string> keep = new List<string>();
                    foreach (Rec r in checkedRecs) if (r.Soft == target.Name) keep.Add(r.Full);
                    if (keep.Count > 0)
                    {
                        HashSet<string> ks = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
                        checkedRecs.RemoveWhere(delegate(Rec r) { return r.Soft == target.Name; });
                        foreach (Rec r in target.Files) if (ks.Contains(r.Full)) checkedRecs.Add(r);
                    }
                    target.State = 2;
                    refreshing = false;
                    if (btnRefresh != null) btnRefresh.Enabled = true;
                    RebuildChoices();
                    RebuildView(true);
                    RefreshSoftRow(target);
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        // 刷新视图但不重建分组
        void BumpView()
        {
            if (currentSoft == null) return;
            RebuildView(false);
            UpdateStatus();
        }

        // ------------------------------------------------ 单软件扫描
        void StartSoftScan(Soft s, bool force)
        {
            if (s == null) return;
            if (s.State == 2 && !force) { RebuildView(true); return; }
            scanTarget = s;
            if (force)
            {
                lock (s.Files) { s.Files.Clear(); s.TotalSize = 0; }
                s.Truncated = false;
                s.State = 0;
            }
            if (s.State != 1)
            {
                s.State = 1;
                RefreshSoftRow(s);
                Thread t = new Thread(delegate() { RunSoftScan(s); });
                t.IsBackground = true;
                t.Start();
            }
        }

        void RunSoftScan(Soft s)
        {
            try
            {
                FileScanner.Scan(s, MAX_FILES_PER_SOFT, delegate() { return scanTarget != s; });
                if (scanTarget == s) s.State = 2;
            }
            catch { if (scanTarget == s) s.State = 2; }
            this.BeginInvoke(new Action(delegate
            {
                if (scanTarget == s) s.State = 2;
                RefreshSoftRow(s);
                if (currentSoft == s) { RebuildChoices(); RebuildView(true); }
            }));
        }

        void RefreshSoftRow(Soft s)
        {
            int idx = softView.IndexOf(s);
            if (idx >= 0) lvSofts.RedrawItems(idx, idx, true);
        }

        void OnTick()
        {
            tickCount++;
            Soft s = currentSoft;
            if (s == null) return;
            if (s.State == 1)
            {
                lblStatus.Text = "正在扫描 " + s.Name + "：" + s.Files.Count.ToString("N0") + " 个文件 / " +
                                 Rules.SizeText(s.TotalSize);
                if (tickCount % 3 == 0) { RebuildView(false); RefreshSoftRow(s); }
            }
        }

        // ------------------------------------------------ 视图
        void OnViewChange()
        {
            RebuildChoices();
            flowFilter.Visible = cmbView.SelectedIndex != 0;
            RebuildView(true);
        }

        void RebuildChoices()
        {
            int mode = cmbView.SelectedIndex;
            cmbCat.Items.Clear();
            if (mode == 0)
            {
                cmbCat.Enabled = false;
                cmbCat.SelectedIndex = -1;
                return;
            }
            CatChoice all = new CatChoice();
            all.Name = "";
            all.Label = "全部";
            if (currentSoft != null) all.Count = currentSoft.Files.Count;
            cmbCat.Items.Add(all);

            if (mode == 1)
            {
                Dictionary<string, int> dict = new Dictionary<string, int>();
                if (currentSoft != null)
                {
                    lock (currentSoft.Files)
                    {
                        foreach (Rec r in currentSoft.Files)
                        {
                            int c;
                            dict.TryGetValue(r.Category, out c);
                            dict[r.Category] = c + 1;
                        }
                    }
                }
                string[] cats = Rules.AllCats;
                foreach (string cat in cats)
                {
                    int c;
                    dict.TryGetValue(cat, out c);
                    CatChoice ch = new CatChoice();
                    ch.Name = cat;
                    ch.Label = cat;
                    ch.Count = c;
                    cmbCat.Items.Add(ch);
                }
            }
            else
            {
                Dictionary<string, int> dict = new Dictionary<string, int>();
                if (currentSoft != null)
                {
                    lock (currentSoft.Files)
                    {
                        foreach (Rec r in currentSoft.Files)
                        {
                            int c;
                            dict.TryGetValue(r.Ext, out c);
                            dict[r.Ext] = c + 1;
                        }
                    }
                }
                List<string> keys = new List<string>(dict.Keys);
                keys.Sort(delegate(string a, string b)
                {
                    int c = dict[b].CompareTo(dict[a]);
                    if (c != 0) return c;
                    return string.Compare(a, b, StringComparison.Ordinal);
                });
                foreach (string k in keys)
                {
                    CatChoice ch = new CatChoice();
                    ch.Name = k;
                    ch.Label = Rules.ExtLabel(k);
                    ch.Count = dict[k];
                    cmbCat.Items.Add(ch);
                }
            }
            cmbCat.Enabled = true;
            if (cmbCat.SelectedIndex < 0) cmbCat.SelectedIndex = 0;
        }

        void RebuildView(bool doSort)
        {
            if (lvFiles == null) return;
            List<Rec> src = new List<Rec>();
            if (currentSoft != null)
            {
                lock (currentSoft.Files) src = new List<Rec>(currentSoft.Files);
            }

            int mode = cmbView.SelectedIndex;
            string filter = "";
            CatChoice ch = cmbCat.SelectedItem as CatChoice;
            if (mode != 0 && ch != null) filter = ch.Name;
            string kw = txtSearch.Text.Trim();

            List<Rec> res = new List<Rec>();
            long total = 0;
            int prot = 0;
            foreach (Rec r in src)
            {
                if (mode == 1 && filter.Length > 0 && r.Category != filter) continue;
                if (mode == 2 && filter.Length > 0 && r.Ext != filter) continue;
                if (kw.Length > 0 &&
                    r.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.Rel.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) continue;
                res.Add(r);
                total += r.SizeBytes;
                if (r.Risk == Rules.PROTECTED) prot++;
            }

            if (doSort) SortRecs(res);

            viewRecs = res;
            viewSize = total;
            viewProtected = prot;
            int scrollRow = 0;
            try { if (lvFiles.TopItem != null) scrollRow = lvFiles.TopItem.Index; } catch { }
            lvFiles.VirtualListSize = 0;
            lvFiles.VirtualListSize = res.Count;
            lvFiles.Invalidate();

            BuildQuickBar(mode);
            UpdateStatus();
        }

        void SortRecs(List<Rec> res)
        {
            string key = cmbSort.Text;
            bool desc = chkDesc.Checked;
            res.Sort(delegate(Rec a, Rec b)
            {
                int c;
                if (key == "大小") c = a.SizeBytes.CompareTo(b.SizeBytes);
                else if (key == "修改时间") c = a.MTime.CompareTo(b.MTime);
                else if (key == "分类") c = string.Compare(a.Category, b.Category, StringComparison.Ordinal);
                else if (key == "类型") c = string.Compare(a.Ext, b.Ext, StringComparison.Ordinal);
                else if (key == "风险") c = Rules.RiskRank(a.Risk).CompareTo(Rules.RiskRank(b.Risk));
                else c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
                return desc ? -c : c;
            });
        }

        void BuildQuickBar(int mode)
        {
            StringBuilder sig = new StringBuilder();
            sig.Append(mode).Append('|');
            int n = cmbCat.Items.Count;
            for (int i = 0; i < n; i++)
            {
                CatChoice c = cmbCat.Items[i] as CatChoice;
                if (c != null) sig.Append(c.Label).Append(c.Count).Append(';');
            }
            string s = sig.ToString();
            if (s == lastBarSig) return;
            lastBarSig = s;

            flowFilter.SuspendLayout();
            flowFilter.Controls.Clear();
            barBusy = true;
            int avail = flowFilter.ClientSize.Width - 14;
            if (avail < 120) avail = 120;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                CatChoice c = cmbCat.Items[i] as CatChoice;
                if (c == null) continue;
                string txt = c.Label + " " + c.Count.ToString("N0");
                int need = TextRenderer.MeasureText(txt, this.Font).Width + 26;
                if (i > 0 && used + need > avail) break;   // 放不下就停，避免按钮被切掉
                Button b = new Button();
                b.Text = txt;
                b.AutoSize = true;
                b.Height = 24;
                b.Margin = new Padding(3, 2, 3, 0);
                b.FlatStyle = FlatStyle.System;
                b.Tag = i;
                if (i == cmbCat.SelectedIndex) b.ForeColor = Color.FromArgb(30, 90, 200);
                b.Click += delegate(object sender, EventArgs ev)
                {
                    cmbCat.SelectedIndex = (int)((Button)sender).Tag;
                };
                flowFilter.Controls.Add(b);
                used += need + 6;
            }
            flowFilter.ResumeLayout();
            barBusy = false;
        }

        void UpdateStatus()
        {
            string soft = currentSoft == null ? "未选择软件" : currentSoft.Name;
            string state = "";
            if (currentSoft != null)
            {
                if (currentSoft.State == 1) state = "（扫描中 " + currentSoft.Files.Count.ToString("N0") + "）";
                else if (currentSoft.Truncated) state = "（扫描被中断）";
            }
            lblStatus.Text = "软件 " + softs.Count.ToString("N0") + " 个  |  " + soft + state +
                "  |  当前视图 " + viewRecs.Count.ToString("N0") + " 个 / " + Rules.SizeText(viewSize) +
                "  |  ● 影响使用 " + viewProtected.ToString("N0") + "  |  已勾选 " + checkedRecs.Count.ToString("N0");
        }

        // ------------------------------------------------ 操作
        void SetAllChecked(bool on)
        {
            if (on) foreach (Rec r in viewRecs) checkedRecs.Add(r);
            else checkedRecs.Clear();
            lvFiles.Invalidate();
            UpdateStatus();
        }

        List<Rec> SelectedRecs()
        {
            List<Rec> res = new List<Rec>();
            foreach (Rec r in viewRecs) if (checkedRecs.Contains(r)) res.Add(r);
            return res;
        }

        void OpenLocation(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
        }

        void OpenSelectedLocation()
        {
            int idx = -1;
            if (lvFiles.SelectedIndices.Count > 0) idx = lvFiles.SelectedIndices[0];
            if (idx >= 0 && idx < viewRecs.Count) { OpenLocation(viewRecs[idx].Full); return; }
            if (checkedRecs.Count > 0)
            {
                foreach (Rec r in viewRecs) if (checkedRecs.Contains(r)) { OpenLocation(r.Full); return; }
            }
            MessageBox.Show(this, "请先选择或勾选一个文件。", "提示");
        }

        void ArchiveSelected()
        {
            List<Rec> sel = SelectedRecs();
            if (sel.Count == 0) { MessageBox.Show(this, "请先勾选要处理的文件。", "提示"); return; }
            List<string> failed = new List<string>();
            int ok = Archiver.Archive(sel, appDir, failed);
            HashSet<Rec> set = new HashSet<Rec>(sel);
            if (currentSoft != null)
            {
                lock (currentSoft.Files)
                {
                    currentSoft.Files.RemoveAll(delegate(Rec r) { return set.Contains(r); });
                    currentSoft.TotalSize = 0;
                    foreach (Rec r in currentSoft.Files) currentSoft.TotalSize += r.SizeBytes;
                }
            }
            checkedRecs.Clear();
            RebuildView(true);
            RefreshSoftRow(currentSoft);
            string msg = "已归档 " + ok + " 个文件到隔离区（可随时还原）。";
            if (failed.Count > 0) msg += "\r\n失败：\r\n" + string.Join("\r\n", failed.Take(10).ToArray());
            MessageBox.Show(this, msg, "归档完成");
        }

        void RestoreArchived()
        {
            RestoreForm dlg = new RestoreForm(appDir);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            List<string> failed = new List<string>();
            int ok = Archiver.Restore(appDir, dlg.Chosen, failed);
            string msg = "已还原 " + ok + " 个文件到原位置。";
            if (failed.Count > 0) msg += "\r\n失败：" + string.Join("\r\n", failed.Take(10).ToArray());
            MessageBox.Show(this, msg, "还原完成");
            if (currentSoft != null) StartSoftScan(currentSoft, true);
        }

        void DeleteSelected()
        {
            List<Rec> sel = SelectedRecs();
            if (sel.Count == 0) { MessageBox.Show(this, "请先勾选要删除的文件。", "提示"); return; }

            List<Rec> prot = new List<Rec>();
            foreach (Rec r in sel) if (r.Risk == Rules.PROTECTED) prot.Add(r);

            bool force = false;
            if (prot.Count > 0)
            {
                ConfirmForm dlg = new ConfirmForm(prot);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                force = dlg.ForceAll;
            }
            else
            {
                DialogResult dr = MessageBox.Show(this,
                    "将彻底删除 " + sel.Count + " 个文件（不可还原）。\r\n建议先「归档所选」放进隔离区，确认无误后再清理。\r\n是否继续？",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (dr != DialogResult.Yes) return;
            }

            List<string> failed = new List<string>();
            List<string> kept = new List<string>();
            int ok = Archiver.Delete(sel, force, failed, kept);

            HashSet<Rec> gone = new HashSet<Rec>();
            foreach (Rec r in sel)
            {
                if (!force && r.Risk == Rules.PROTECTED) continue;
                bool bad = false;
                foreach (string f in failed) if (f.StartsWith(r.Name + "（")) { bad = true; break; }
                if (bad) continue;
                gone.Add(r);
            }
            if (currentSoft != null && gone.Count > 0)
            {
                lock (currentSoft.Files)
                {
                    currentSoft.Files.RemoveAll(delegate(Rec r) { return gone.Contains(r); });
                    currentSoft.TotalSize = 0;
                    foreach (Rec r in currentSoft.Files) currentSoft.TotalSize += r.SizeBytes;
                }
            }
            checkedRecs.Clear();
            RebuildView(true);
            RefreshSoftRow(currentSoft);

            string msg = "已彻底删除 " + ok + " 个文件。";
            if (kept.Count > 0) msg += "\r\n已保留 " + kept.Count + " 个受保护文件（勾选风险确认后才可删除）。";
            if (failed.Count > 0) msg += "\r\n失败：" + string.Join("\r\n", failed.Take(10).ToArray());
            MessageBox.Show(this, msg, "删除完成");
        }

        void OpenArchiveDir()
        {
            string dir = Archiver.Root(appDir);
            try
            {
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开失败"); }
        }
    }

    // ---------------------------------------------------------------- 自测
    static class SelfTest
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "dirty_selftest_" + Guid.NewGuid().ToString("N"));
            string app = Path.Combine(Path.GetTempPath(), "dirty_selftest_app_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "TestApp1", "cache"));
                Directory.CreateDirectory(Path.Combine(root, "TestApp1", "logs"));
                Directory.CreateDirectory(Path.Combine(root, "TestApp1", "save"));
                Directory.CreateDirectory(Path.Combine(root, "TestApp2"));
                Directory.CreateDirectory(app);

                Write(Path.Combine(root, "TestApp1", "cache", "a.tmp"), 100);
                Write(Path.Combine(root, "TestApp1", "logs", "b.log"), 50);
                Write(Path.Combine(root, "TestApp1", "save", "c.sav"), 200);
                Write(Path.Combine(root, "TestApp1", "app.exe"), 1000);
                Write(Path.Combine(root, "TestApp2", "backup.zip"), 300);
                Write(Path.Combine(root, "TestApp2", "data.dat"), 80);

                Soft s1 = new Soft();
                s1.Name = "TestApp1";
                s1.AddPath(Path.Combine(root, "TestApp1"));
                FileScanner.Scan(s1, 100000, null);
                Soft s2 = new Soft();
                s2.Name = "TestApp2";
                s2.AddPath(Path.Combine(root, "TestApp2"));
                FileScanner.Scan(s2, 100000, null);

                if (s1.Files.Count != 4) { Console.WriteLine("FAIL count=" + s1.Files.Count); return 1; }
                Dictionary<string, Rec> by = new Dictionary<string, Rec>();
                foreach (Rec r in s1.Files) by[r.Name] = r;
                foreach (Rec r in s2.Files) by[r.Name] = r;

                if (by["app.exe"].Risk != Rules.PROTECTED) { Console.WriteLine("FAIL exe " + by["app.exe"].Risk); return 1; }
                if (by["c.sav"].Risk != Rules.PROTECTED) { Console.WriteLine("FAIL sav " + by["c.sav"].Risk + " " + by["c.sav"].Category); return 1; }
                if (by["a.tmp"].Risk != Rules.SAFE) { Console.WriteLine("FAIL tmp " + by["a.tmp"].Risk); return 1; }
                if (by["backup.zip"].Risk != Rules.CAUTION) { Console.WriteLine("FAIL zip"); return 1; }
                if (by["b.log"].Category != Rules.LOG) { Console.WriteLine("FAIL log cat"); return 1; }
                if (by["data.dat"].Ext != ".dat") { Console.WriteLine("FAIL ext"); return 1; }
                if (by["app.exe"].Ext != ".exe") { Console.WriteLine("FAIL type"); return 1; }

                List<string> failed = new List<string>();
                List<Rec> one = new List<Rec>();
                one.Add(by["a.tmp"]);
                int moved = Archiver.Archive(one, app, failed);
                if (moved != 1 || failed.Count != 0) { Console.WriteLine("FAIL archive"); return 1; }
                if (File.Exists(Path.Combine(root, "TestApp1", "cache", "a.tmp"))) { Console.WriteLine("FAIL not moved"); return 1; }
                List<ManifestEntry> man = Archiver.Load(app);
                if (man.Count != 1) { Console.WriteLine("FAIL manifest"); return 1; }
                List<string> f2 = new List<string>();
                int back = Archiver.Restore(app, man, f2);
                if (back != 1) { Console.WriteLine("FAIL restore"); return 1; }
                if (!File.Exists(Path.Combine(root, "TestApp1", "cache", "a.tmp"))) { Console.WriteLine("FAIL not back"); return 1; }
                if (Archiver.Load(app).Count != 0) { Console.WriteLine("FAIL manifest clear"); return 1; }

                List<Rec> two = new List<Rec>();
                two.Add(by["app.exe"]);
                List<string> f3 = new List<string>();
                List<string> kept = new List<string>();
                int d0 = Archiver.Delete(two, false, f3, kept);
                if (d0 != 0 || kept.Count != 1) { Console.WriteLine("FAIL protect skip"); return 1; }
                if (!File.Exists(by["app.exe"].Full)) { Console.WriteLine("FAIL deleted protected"); return 1; }
                int d1 = Archiver.Delete(two, true, f3, kept);
                if (d1 != 1) { Console.WriteLine("FAIL force delete"); return 1; }

                // 外部删除后刷新：残留记录要被清掉
                int before = s2.Files.Count;
                File.Delete(Path.Combine(root, "TestApp2", "data.dat"));
                int purged = FileScanner.PurgeMissing(s2);
                if (purged != 1 || s2.Files.Count != before - 1) { Console.WriteLine("FAIL purge=" + purged); return 1; }
                if (s2.TotalSize != 300) { Console.WriteLine("FAIL purge size=" + s2.TotalSize); return 1; }

                Console.WriteLine("SELFTEST OK scan/rules/type/archive/restore/protect/purge");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(app, true); } catch { }
            }
        }

        static void Write(string p, int size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new byte[size]);
        }
    }

    static class Program
    {
        static void DumpEnum()
        {
            List<string> roots = new List<string>();
            string[] defs = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string d in defs)
                if (d != null && d.Length > 0 && Directory.Exists(d) && !roots.Contains(d)) roots.Add(d);
            int errs;
            List<Soft> all = SoftFinder.Enumerate(roots, true, delegate { }, delegate { return false; }, out errs);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("软件数=" + all.Count);
            foreach (Soft s in all)
                sb.AppendLine(s.Name + " | " + s.From + " | " + (s.Paths.Count > 0 ? s.Paths[0] : "-"));
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "dirty_dump.txt"), sb.ToString(), Encoding.UTF8);
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }
            if (args.Length > 0 && args[0] == "--dump")
            {
                DumpEnum();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--preview")
            {
                MainForm f = new MainForm();
                f.Show();
                f.PreviewSelect(args.Length > 1 ? args[1] : "7-Zip", args.Length > 2 ? int.Parse(args[2]) : 0);
                Application.Run(f);
                return;
            }
            if (args.Length > 0 && args[0] == "--uitest")
            {
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                string r = f.RunUiTest();
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "dirty_uitest.txt"), r, Encoding.UTF8); }
                catch { }
                f.Close();
                f.Dispose();
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
