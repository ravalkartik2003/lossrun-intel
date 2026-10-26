using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LossRun.Tool;

/// <summary>
/// <c>lossrun demo pack --out &lt;dir&gt; [--skip-build] [--label "…"]</c> (brief 13 addendum): a self-contained offline demo.
/// It contains the workbench built in mock mode (recorded API responses and upload streams, synthetic data, no
/// network), a zero-dependency Node static server, and the deck table, acceptance table, demo script and screenshots.
/// Run it with <c>node serve.mjs</c>. The pack must not name an Azure resource: it is scanned, and the command fails
/// if any resource name is found.
/// </summary>
internal static partial class DemoPack
{
    /// <summary>The client's name, the POC naming prefix and Azure service hostnames: none may ship (the bundled public-suffix list has none of these dotted forms).</summary>
    [GeneratedRegex(@"State Fund|SCIF|lossrun-poc|\.azurewebsites\.net|\.azurestaticapps\.net|\.vault\.azure\.net|\.database\.windows\.net|\.blob\.core\.windows\.net|\.cognitiveservices\.azure\.com|\.search\.windows\.net|\.servicebus\.windows\.net", RegexOptions.IgnoreCase)] // generic-guard: allow (the scan that keeps these names out)
    internal static partial Regex Forbidden();

    public static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (Option("--out") is not { } outDir)
        {
            Console.Error.WriteLine("usage: lossrun demo pack --out <dir> [--skip-build] [--label \"Independent POC · synthetic data\"]");
            return 2;
        }

        var root = RepoRoot();
        var workbench = Path.Combine(root, "src", "web", "apps", "workbench");
        if (!args.Contains("--skip-build"))
        {
            var label = Option("--label");
            var exit = await RunAsync(OperatingSystem.IsWindows() ? "cmd.exe" : "npm", OperatingSystem.IsWindows() ? "/c npm run build:mock" : "run build:mock", workbench,
                label is null ? [] : [("VITE_ENV_LABEL", label)]);
            if (exit != 0)
            {
                Console.Error.WriteLine("build:mock failed");
                return exit;
            }
        }

        var target = Path.GetFullPath(outDir);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Copy(Path.Combine(workbench, "dist-mock"), Path.Combine(target, "app"));

        // The packed app talks to nothing but itself: connect-src 'self' (the live build also allows the API and Entra).
        var swaConfig = Path.Combine(target, "app", "staticwebapp.config.json");
        if (File.Exists(swaConfig))
        {
            var text = File.ReadAllText(swaConfig);
            File.WriteAllText(swaConfig, ConnectSrc().Replace(text, "connect-src 'self';"));
        }

        // Brief 25: the quote system's loss-information page with its recorded sequence, for the Integration moment's fallback:
        // /quote-system/loss-information.html?quote=Q-DEMO-REPLAY&replay=1
        var quoteSystem = Path.Combine(target, "app", "quote-system");
        Directory.CreateDirectory(quoteSystem);
        foreach (var file in new[] { "loss-information.html", "loss-information.js", "replay.json" })
        {
            File.Copy(Path.Combine(root, "src", "LossRun.QuoteSystemMock", "wwwroot", file), Path.Combine(quoteSystem, file));
        }

        var docs = Path.Combine(target, "docs");
        Directory.CreateDirectory(docs);
        foreach (var (from, to) in new[]
        {
            ("docs/evaluation/results/2026-10-23-corpus-v3/deck-table.md", "deck-table.md"),
            ("docs/poc-plan/closeout/acceptance-table.md", "acceptance-table.md"),
            ("docs/poc-plan/demo-script.md", "demo-script.md"),
        })
        {
            File.Copy(Path.Combine(root, from), Path.Combine(docs, to));
        }

        Copy(Path.Combine(root, "docs", "ui", "screenshots"), Path.Combine(target, "screenshots"));
        File.WriteAllText(Path.Combine(target, "serve.mjs"), ServeScript);
        File.WriteAllText(Path.Combine(target, "README.md"), Readme);

        var leaks = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".js" or ".mjs" or ".json" or ".md" or ".html" or ".css" or ".txt" or ".svg")
            .SelectMany(f => Forbidden().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetRelativePath(target, f)}: {m.Value}"))
            .Distinct().ToList();
        if (leaks.Count > 0)
        {
            Console.Error.WriteLine($"demo pack names the client or Azure resources ({leaks.Count}); fix the source, not the pack:");
            leaks.Take(20).ToList().ForEach(l => Console.Error.WriteLine("  " + l));
            return 1;
        }

        var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"demo pack: {files.Count} files, {files.Sum(f => new FileInfo(f).Length) / 1024 / 1024.0:0.0} MB → {target}");
        Console.WriteLine("run: node serve.mjs   (then open http://localhost:4173)");
        return 0;
    }

    [GeneratedRegex(@"connect-src [^;]*;")]
    private static partial Regex ConnectSrc();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && Directory.Exists(Path.Combine(dir.FullName, "src", "web")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("run from inside the repository");
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static async Task<int> RunAsync(string file, string arguments, string workingDirectory, IEnumerable<(string Name, string Value)> environment)
    {
        var start = new ProcessStartInfo(file, arguments) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>A static server with the SPA fallback and the app's security headers; Node built-ins only, localhost only.</summary>
    private const string ServeScript = """
        // Offline demo server (lossrun demo pack). Node 18+, no packages, no network: serves ./app on http://localhost:4173.
        import { createServer } from "node:http";
        import { readFile, stat } from "node:fs/promises";
        import { extname, join, normalize } from "node:path";
        import { fileURLToPath } from "node:url";

        const root = join(fileURLToPath(new URL(".", import.meta.url)), "app");
        const port = Number(process.env.PORT ?? 4173);
        const config = JSON.parse(await readFile(join(root, "staticwebapp.config.json"), "utf8").catch(() => "{}"));
        const headers = config.globalHeaders ?? {};
        const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css", ".json": "application/json",
          ".svg": "image/svg+xml", ".png": "image/png", ".jpg": "image/jpeg", ".woff": "font/woff", ".woff2": "font/woff2", ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document" };

        createServer(async (req, res) => {
          const path = normalize(decodeURIComponent(new URL(req.url ?? "/", "http://localhost").pathname)).replace(/^([/\\])+/, "");
          let file = join(root, path);
          if (!file.startsWith(root) || !(await stat(file).then((s) => s.isFile(), () => false))) file = join(root, "index.html"); // SPA fallback
          try {
            const body = await readFile(file);
            res.writeHead(200, { ...headers, "Content-Type": types[extname(file)] ?? "application/octet-stream" });
            res.end(body);
          } catch {
            res.writeHead(404).end();
          }
        }).listen(port, "127.0.0.1", () => console.log(`Offline demo: http://localhost:${port}  (Ctrl+C to stop)`));
        """;

    private const string Readme = """
        # Loss Run Intelligence: offline demo

        The workbench runs here on recorded responses and synthetic data. It needs no network and no sign-in; a **Role** picker sits in the top bar.

        ## Run

        1. Install Node 18 or later (the only prerequisite).
        2. In this folder: `node serve.mjs`
        3. Open http://localhost:4173

        Changes you make (overrides, context, what-if) live in the browser's memory. Reload the page to reset them.

        ## Contents

        | Path | What it is |
        |---|---|
        | `app/` | The workbench, built in offline mode |
        | `app/quote-system/` | The mock quote system's loss-information page and its recording (open `/quote-system/loss-information.html?quote=Q-DEMO-REPLAY&replay=1`) |
        | `serve.mjs` | A local static server (Node built-ins only, bound to 127.0.0.1) |
        | `docs/demo-script.md` | The 25-minute demo, with exact clicks; use its *Fallback* table on this build |
        | `docs/deck-table.md` | Accuracy by input type on the synthetic corpus, with confidence intervals |
        | `docs/acceptance-table.md` | Every acceptance criterion and its measured result |
        | `screenshots/` | Key screens in light and dark themes |

        Everything shown is synthetic: fictional carriers, generated documents, no client data.
        """;
}
