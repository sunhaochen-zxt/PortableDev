using System.Collections.Generic;

namespace PortableDev.Core.Packages;

/// <summary>One hand-picked entry on the packages home page.</summary>
public sealed record CatalogEntry(string App, string Bucket, string Category);

/// <summary>
/// The "recommended" shelf. Scoop has no install counter, so this list is curated by hand
/// and then ranked by the GitHub stars of each project (see <see cref="ScoopStars"/>):
/// the ordering is real data, the selection is editorial.
/// </summary>
public static class ScoopCatalog
{
    public static IReadOnlyList<CatalogEntry> Featured { get; } = new[]
    {
        // 终端 / shell
        new CatalogEntry("wezterm", "Extras", "终端"),
        new CatalogEntry("alacritty", "Extras", "终端"),
        new CatalogEntry("tabby", "Extras", "终端"),
        new CatalogEntry("fzf", "Main", "终端"),
        new CatalogEntry("bat", "Main", "终端"),
        new CatalogEntry("eza", "Main", "终端"),
        new CatalogEntry("zoxide", "Main", "终端"),
        new CatalogEntry("starship", "Main", "终端"),
        new CatalogEntry("oh-my-posh", "Main", "终端"),
        new CatalogEntry("delta", "Main", "终端"),
        new CatalogEntry("btop", "Main", "终端"),

        // 开发工具
        new CatalogEntry("neovim", "Main", "开发"),
        new CatalogEntry("helix", "Main", "开发"),
        new CatalogEntry("vscode", "Extras", "开发"),
        new CatalogEntry("lazygit", "Extras", "开发"),
        new CatalogEntry("gitui", "Main", "开发"),
        new CatalogEntry("gh", "Main", "开发"),
        new CatalogEntry("ripgrep", "Main", "开发"),
        new CatalogEntry("fd", "Main", "开发"),
        new CatalogEntry("git", "Main", "开发"),
        new CatalogEntry("cmake", "Main", "开发"),
        new CatalogEntry("ninja", "Main", "开发"),
        new CatalogEntry("nodejs", "Main", "开发"),
        new CatalogEntry("python", "Main", "开发"),
        new CatalogEntry("go", "Main", "开发"),
        new CatalogEntry("rustup", "Main", "开发"),
        new CatalogEntry("uv", "Main", "开发"),
        new CatalogEntry("deno", "Main", "开发"),
        new CatalogEntry("pandoc", "Main", "开发"),
        new CatalogEntry("llvm", "Main", "开发"),

        // 系统工具
        new CatalogEntry("everything", "Extras", "系统"),
        new CatalogEntry("powertoys", "Extras", "系统"),
        new CatalogEntry("wiztree", "Extras", "系统"),
        new CatalogEntry("crystaldiskinfo", "Extras", "系统"),
        new CatalogEntry("7zip", "Main", "系统"),
        new CatalogEntry("notepadplusplus", "Extras", "编辑"),
        new CatalogEntry("sublime-text", "Extras", "编辑"),
        new CatalogEntry("micro", "Main", "编辑"),

        // 网络 / 媒体
        new CatalogEntry("winscp", "Extras", "网络"),
        new CatalogEntry("putty", "Extras", "网络"),
        new CatalogEntry("aria2", "Main", "网络"),
        new CatalogEntry("curl", "Main", "网络"),
        new CatalogEntry("wget", "Main", "网络"),
        new CatalogEntry("yq", "Main", "网络"),
        new CatalogEntry("jq", "Main", "网络"),
        new CatalogEntry("ffmpeg", "Main", "媒体"),
        new CatalogEntry("mpv", "Extras", "媒体"),
        new CatalogEntry("vlc", "Extras", "媒体"),
        new CatalogEntry("obs-studio", "Extras", "媒体"),
        new CatalogEntry("imagemagick", "Main", "媒体"),
        new CatalogEntry("sharex", "Extras", "媒体"),
    };
}
