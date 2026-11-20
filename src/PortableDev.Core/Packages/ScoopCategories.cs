using System;
using System.Collections.Generic;
using System.Linq;

namespace PortableDev.Core.Packages;

/// <summary>
/// Categories for browsing every bucket app, plus the curated overrides.
///
/// Scoop manifests carry no category field, so the only signal that exists for all ~4,200
/// apps is the name itself. Keyword rules are applied to the app id (never the
/// description, which is not fetched for a whole page), and the curated shelf wins when it
/// names an app, because that label was written by a human.
///
/// The classification is deliberately conservative: a wrong guess only moves a card to a
/// different shelf, an app never disappears — anything unmatched lands in 其他.
/// </summary>
public static class ScoopCategories
{
    public const string All = "全部";
    public const string Other = "其他";

    public static IReadOnlyList<string> Order { get; } = new[]
    {
        All, "开发", "终端", "编辑", "系统", "网络", "媒体", "美化", Other,
    };

    private static readonly Dictionary<string, string> Curated =
        ScoopCatalog.Featured.ToDictionary(e => e.App, e => e.Category, StringComparer.OrdinalIgnoreCase);

    private static readonly (string Category, string[] Keywords)[] Rules =
    {
        ("终端", new[]
        {
            "terminal", "console", "conemu", "cmder", "tabby", "wezterm", "alacritty", "kitty",
            "switcher", "shell", "pwsh", "powershell", "zoxide", "fzf", "bat", "eza", "lsd",
            "ripgrep", "rg", "fd", "delta", "starship", "oh-my-posh", "btop", "bottom", "htop",
            "neofetch", "fastfetch", "windows-terminal", "fluentterminal", "tmux", "mosh",
            "direnv", "jq", "yq", "less", "more", "tldr", "cheat", "wt", "act",
        }),
        ("开发", new[]
        {
            "python", "pyenv", "pip", "uv", "poetry", "nodejs", "node", "npm", "pnpm", "yarn",
            "deno", "bun", "typescript", "golang", "go", "rust", "rustup", "cargo", "zig", "nim",
            "java", "jdk", "openjdk", "maven", "gradle", "kotlin", "scala", "sbt", "swift",
            "dotnet", "nuget", "msbuild", "sdk", "llvm", "clang", "gcc", "mingw", "msys",
            "cmake", "ninja", "make", "meson", "conan", "vcpkg", "git", "git-lfs", "gh",
            "gitui", "lazygit", "tig", "docker", "podman", "kubectl", "k9s", "helm", "terraform",
            "ansible", "vagrant", "neovim", "nvim", "vim", "emacs", "helix", "vscode",
            "vscodium", "eclipse", "idea", "pycharm", "webstorm", "android", "flutter", "dart",
            "php", "composer", "ruby", "perl", "lua", "luajit", "sqlite", "postgres", "mysql",
            "redis", "mongodb", "nginx", "apache", "node-gyp", "eslint", "prettier", "makefile",
            "assembly", "nasm", "yasm", "wix", "nsis", "innosetup", "ilspy", "link",
        }),
        ("编辑", new[]
        {
            "editor", "notepad", "notepadplusplus", "npp", "sublime", "text", "hex", "emeditor",
            "typora", "marktext", "obsidian", "logseq", "markdown", "pandoc", "writer",
            "vim", "emacs", "micro", "gedit", "codeblocks", "scite", "akelpad", "kate",
        }),
        ("系统", new[]
        {
            "7zip", "zip", "unzip", "rar", "7z", "arc", "tar", "compress", "peazip",
            "disk", "partition", "defrag", "clean", "ccleaner", "uninstall", "geek",
            "registry", "autoruns", "process", "task", "explorer", "everything", "wiztree",
            "treesize", "crystaldisk", "hwinfo", "cpu-z", "cpuz", "gpu-z", "gpuz", "aida",
            "system", "powertoys", "sysinternals", "driver", "backup", "restore", "veeam",
            "rufus", "ventoy", "etcher", "balena", "usb", "sdcard", "format", "file", "copy",
            "robocopy", "teracopy", "filehash", "sha", "checksum", "winget", "choco", "scoop",
            "sdelete", "sysinternals", "sandboxie", "virtualbox", "vmware", "qemu", "wsl",
            "nvm", "sdkman", "scoop", "clover", "startup", "cleanmgr", "dism", "sfc",
        }),
        ("网络", new[]
        {
            "browser", "firefox", "chrome", "chromium", "edge", "brave", "opera", "vivaldi",
            "thunderbird", "mail", "outlook", "download", "curl", "wget", "aria2", "putty",
            "winscp", "filezilla", "ftp", "sftp", "ssh", "vpn", "wireguard", "openvpn",
            "clash", "v2ray", "proxy", "torrent", "qbittorrent", "transmission", "deluge",
            "telegram", "discord", "zoom", "teams", "slack", "wechat", "qq", "remote", "rdp",
            "mstsc", "anydesk", "teamviewer", "rustdesk", "syncthing", "wireshark", "nmap",
            "ping", "traceroute", "dns", "http", "postman", "insomnia", "ngrok", "frp",
        }),
        ("媒体", new[]
        {
            "player", "video", "audio", "music", "vlc", "mpv", "potplayer", "foobar", "aimp",
            "spotify", "obs", "handbrake", "ffmpeg", "imagemagick", "gimp", "paint", "krita",
            "inkscape", "blender", "audacity", "shotcut", "kdenlive", "screenshot", "sharex",
            "greenshot", "capture", "snipaste", "image", "photo", "font", "subtitle", "codec",
        }),
        ("美化", new[]
        {
            "theme", "icon", "wallpaper", "cursor", "fontforge", "nerd", "powerline",
            "rainmeter", "translucent", "taskbar", "startallback", "classic-shell", "accent",
            "wallpaper-engine", "fences",
        }),
    };

    /// <summary>Category of one app id; curated labels first, then keyword rules.</summary>
    public static string Classify(string app)
    {
        var raw = (app ?? string.Empty).Trim();
        if (raw.Length == 0) return Other;

        if (Curated.TryGetValue(raw, out var curated)) return curated;

        var name = raw.ToLowerInvariant();
        var tokens = Tokenize(name);

        foreach (var (category, keywords) in Rules)
        {
            foreach (var keyword in keywords)
            {
                if (keyword.Length <= 3)
                {
                    if (tokens.Contains(keyword)) return category;
                    continue;
                }

                if (name.Contains(keyword, StringComparison.Ordinal)) return category;
            }
        }

        return Other;
    }

    private static HashSet<string> Tokenize(string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();

        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else if (current.Length > 0) { set.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) set.Add(current.ToString());

        return set;
    }
}
