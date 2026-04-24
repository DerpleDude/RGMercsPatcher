using LibGit2Sharp;
using System.IO;

namespace RGMercsPatcher;

public class PatcherService
{
    private const string RepoUrl     = "https://github.com/DerpleDude/rgmercs.git";
    private const string CommitsApi  = "https://api.github.com/repos/DerpleDude/rgmercs/commits";

    private static string RepoDir(string luaFolder) => Path.Combine(luaFolder, "rgmercs");

    public async Task SyncAndPatch(string luaFolder, IProgress<(double Percent, string Status)> progress,
        IProgress<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(luaFolder))
            throw new InvalidOperationException("Lua folder is not set.");

        void Report(double pct, string msg) { progress.Report((pct, msg)); log.Report(msg); }

        await Task.Run(() =>
        {
            var repoDir = RepoDir(luaFolder);
            if (!Directory.Exists(repoDir) || !Repository.IsValid(repoDir))
            {
                if (Directory.Exists(repoDir))
                    Directory.Delete(repoDir, recursive: true);
                Directory.CreateDirectory(repoDir);
                Report(0.05, "Cloning RGMercs...");
                var cloneOptions = new CloneOptions();
                cloneOptions.FetchOptions.OnProgress = _ => !ct.IsCancellationRequested;
                cloneOptions.FetchOptions.OnTransferProgress = p =>
                {
                    double pct = p.TotalObjects > 0
                        ? 0.05 + (p.ReceivedObjects / (double)p.TotalObjects) * 0.95
                        : 0.05;
                    progress.Report((pct, $"Cloning... {p.ReceivedObjects}/{p.TotalObjects} objects"));
                    return !ct.IsCancellationRequested;
                };
                Repository.Clone(RepoUrl, repoDir, cloneOptions);
                Report(1.0, "Clone complete.");
            }
            else
            {
                Report(0.05, "Pulling latest RGMercs...");
                using var repo = new Repository(repoDir);
                var remote = repo.Network.Remotes["origin"];
                var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
                Commands.Fetch(repo, remote.Name, refSpecs, null, null);

                var remoteBranch = repo.Branches["origin/main"] ?? repo.Branches["origin/master"];
                if (remoteBranch != null)
                {
                    var branchName = remoteBranch.FriendlyName.Replace("origin/", "");
                    var localBranch = repo.Branches[branchName];
                    if (localBranch == null)
                    {
                        localBranch = repo.CreateBranch(branchName, remoteBranch.Tip);
                        repo.Branches.Update(localBranch, b => b.TrackedBranch = remoteBranch.CanonicalName);
                    }
                    Commands.Checkout(repo, localBranch);
                    repo.Reset(ResetMode.Hard, remoteBranch.Tip);
                    Report(1.0, "Already up to date.");
                }
            }
        }, ct);
    }

    private static List<(string Sha, string Date, string Message)> FetchCommitsFromApi(int perPage)
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "RGMercsPatcher");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        var json = http.GetStringAsync($"{CommitsApi}?per_page={perPage}").GetAwaiter().GetResult();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<(string, string, string)>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var sha = entry.GetProperty("sha").GetString() ?? "";
            var commit = entry.GetProperty("commit");
            var author = commit.GetProperty("author");
            var dateStr = author.GetProperty("date").GetString() ?? "";
            var message = commit.GetProperty("message").GetString()?.TrimEnd() ?? "";
            if (DateTimeOffset.TryParse(dateStr, out var dt))
                dateStr = dt.LocalDateTime.ToString("yyyy-MM-dd");
            result.Add((sha, dateStr, message));
        }
        return result;
    }

    private static string BuildCommitLog(List<(string Sha, string Date, string Message)> commits)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (_, date, msg) in commits)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append($"[{date}]\n{msg}");
        }
        return sb.ToString();
    }

    public (bool HasNew, string CommitLog) FetchAndCheck(string luaFolder)
    {
        List<(string Sha, string Date, string Message)> commits;
        try
        {
            commits = FetchCommitsFromApi(10);
        }
        catch
        {
            return (true, "Could not reach RGMercs repository.");
        }

        if (commits.Count == 0)
            return (true, "No commits found.");

        var remoteSha = commits[0].Sha;

        var repoDir = RepoDir(luaFolder);
        if (!Directory.Exists(repoDir) || !Repository.IsValid(repoDir))
            return (true, BuildCommitLog(commits));

        string localSha;
        using (var repo = new Repository(repoDir))
        {
            var branch = repo.Branches["origin/main"] ?? repo.Branches["origin/master"];
            localSha = branch?.Tip?.Sha ?? repo.Head.Tip?.Sha ?? "";
        }

        if (localSha == remoteSha)
            return (false, BuildCommitLog(commits));

        // Fetch enough commits to cover everything since localSha, minimum 10
        try
        {
            var extended = FetchCommitsFromApi(100);
            var sincIdx = extended.FindIndex(c => c.Sha == localSha);
            var sinceLocal = sincIdx > 0 ? extended.Take(sincIdx).ToList() : extended;
            var toShow = sinceLocal.Count >= 10 ? sinceLocal : extended.Take(Math.Max(10, sinceLocal.Count)).ToList();
            return (true, BuildCommitLog(toShow));
        }
        catch
        {
            return (true, BuildCommitLog(commits));
        }
    }
}
