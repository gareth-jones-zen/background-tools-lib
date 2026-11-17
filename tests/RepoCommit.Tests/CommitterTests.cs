namespace RepoCommit.Tests;

public sealed class CommitterTests : IDisposable
{
    private readonly TempRepo repo = new();
    private readonly List<string> lines = [];

    public void Dispose() => repo.Dispose();

    private Committer Git(int lockRetries = 20, string? folder = null) =>
        new(folder ?? repo.Path, lines.Add, lockRetries: lockRetries, lockWait: TimeSpan.FromMilliseconds(100));

    [Fact]
    public void Only_the_given_paths_are_committed_and_everything_else_is_left_as_it_was()
    {
        repo.Write("notes/old.md", "old\n");
        repo.Write("other/tracked.md", "tracked\n");
        repo.Commit("Set-up", "notes/old.md", "other/tracked.md");
        repo.Write("other/tracked.md", "changed by someone else\n");   // modified, not staged
        repo.Write("other/staged.md", "staged by someone else\n");
        repo.Git("add", "other/staged.md");                            // staged by someone else
        repo.Write("other/new.md", "new, untracked\n");               // untracked
        repo.Write("notes/old.md", "the tool's change\n");
        repo.Write("notes/new.md", "the tool's new file\n");

        var result = Git().Commit("Ingest notes", ["notes/new.md", "notes/old.md"]);

        Assert.True(result.Committed, result.Error);
        Assert.Null(result.Error);
        Assert.Equal(["notes/old.md", "notes/new.md"], result.Paths);   // changed tracked files first, then new ones
        Assert.Equal(["M\tnotes/old.md", "A\tnotes/new.md"], repo.LastFiles.Order().Reverse().ToList());
        Assert.Equal("Ingest notes", repo.LastMessage);
        Assert.Equal(["A  other/staged.md", " M other/tracked.md", "?? other/new.md"], repo.Status);
        Assert.Equal(7, result.Commit!.Length);
        Assert.Equal([$"git: committed 2 path(s) as {result.Commit}"], lines);
    }

    [Fact]
    public void Full_paths_inside_the_repository_are_taken_too()
    {
        repo.Write("a.md", "a\n");
        var result = Git().Commit("A", [repo.File("a.md")]);
        Assert.True(result.Committed);
        Assert.Equal(["A\ta.md"], repo.LastFiles);
    }

    [Theory]
    [InlineData("..\\outside.md")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData(".git/config")]
    [InlineData(".")]
    public void A_path_outside_the_repository_refuses_the_whole_commit(string outside)
    {
        repo.Write("a.md", "a\n");
        var before = repo.Count;
        var result = Git().Commit("A", ["a.md", outside]);
        Assert.False(result.Committed);
        Assert.Contains("isn't inside the repository", result.Error);
        Assert.Equal(before, repo.Count);
        Assert.Equal(["?? a.md"], repo.Status);   // not even staged
    }

    [Fact]
    public void Nothing_changed_is_no_commit_and_no_error()
    {
        repo.Write("a.md", "a\n");
        repo.Commit("Set-up", "a.md");
        repo.Write("a.md", "a\n");   // written again, the same
        var before = repo.Count;
        var result = Git().Commit("A", ["a.md", "missing.md"]);
        Assert.Equal(CommitResult.Nothing, result);
        Assert.Equal(before, repo.Count);
        Assert.Equal(["git: nothing to commit"], lines);
    }

    [Fact]
    public void A_move_is_committed_as_a_rename_and_a_deletion_as_one()
    {
        repo.Write("preps/2026-10-01-x.md", "a prep\nwith some lines\n");
        repo.Write("gone.md", "gone\n");
        repo.Commit("Set-up", "preps/2026-10-01-x.md", "gone.md");
        Directory.CreateDirectory(repo.File("preps/archive/2026-10"));
        File.Move(repo.File("preps/2026-10-01-x.md"), repo.File("preps/archive/2026-10/2026-10-01-x.md"));
        File.Delete(repo.File("gone.md"));

        var result = Git().Commit("Archive 1 prep", [], [new Rename("preps/2026-10-01-x.md", "preps/archive/2026-10/2026-10-01-x.md")]);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(["R100\tpreps/2026-10-01-x.md\tpreps/archive/2026-10/2026-10-01-x.md"], repo.LastFiles);
        Assert.Equal([" D gone.md"], repo.Status);   // not given: left alone

        Assert.True(Git().Commit("Delete", ["gone.md"]).Committed);
        Assert.Equal(["D\tgone.md"], repo.LastFiles);
    }

    [Fact]
    public void Trailers_end_the_message()
    {
        repo.Write("a.md", "a\n");
        Git().Commit("Ingest a\n\nFiled automatically.", ["a.md"], trailers: ["Co-Authored-By: Claude <noreply@anthropic.com>"]);
        Assert.Equal("Ingest a\n\nFiled automatically.\n\nCo-Authored-By: Claude <noreply@anthropic.com>", repo.LastMessage);
        Assert.Equal("S\n\nB\n\nX: 1\nY: 2", Committer.Message("S", "B", ["X: 1", "Y: 2"]));
        Assert.Equal("S", Committer.Message("S", "", []));
        Assert.Equal("S\n\nX: 1", Committer.Message("S", null, ["X: 1"]));
    }

    [Fact]
    public void Pathspecs_are_literal_and_names_can_be_anything_a_file_can_have()
    {
        repo.Write("[ab].md", "brackets\n");
        repo.Write("a.md", "a glob would match this\n");
        repo.Write("Zoë Example's notes.md", "non-ASCII\n");
        var result = Git().Commit("Odd names", ["[ab].md", "Zoë Example's notes.md"]);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(["A\tZoë Example's notes.md", "A\t[ab].md"], repo.LastFiles);   // git's byte order: Z before [
        Assert.Equal(["?? a.md"], repo.Status);
    }

    [Fact]
    public void Changed_is_in_git_statuss_order_even_across_many_paths()
    {
        var names = Enumerable.Range(0, 250).Select(i => $"f/{i:000}.md").ToList();
        foreach (var name in names.Take(10))
            repo.Write(name, "x\n");
        repo.Commit("Set-up", [.. names.Take(10)]);
        foreach (var name in names)
            repo.Write(name, "y\n");
        var given = names.AsEnumerable().Reverse().Append("not/there.md").ToList();
        var changed = Git().Changed(given);
        Assert.Equal([.. names.Take(10), .. names.Skip(10)], changed);   // the 10 tracked first, then the new, each by path
        Assert.Equal(names.Take(10).ToHashSet(), Git().Tracked(given));

        var result = Git().Commit("Many", given);
        Assert.Equal(250, result.Paths.Count);
        Assert.Empty(repo.Status);
    }

    [Fact]
    public void A_folder_inside_the_repository_takes_paths_relative_to_itself()
    {
        repo.Write("tools/settings.toml", "x\n");
        repo.Write("reports/a.md", "a\n");
        var git = Git(folder: repo.File("tools"));
        Assert.Equal(repo.Path, git.Top);
        Assert.Equal("reports/a.md", git.RepoPath("../reports/a.md"));
        Assert.Equal("tools/settings.toml", git.RepoPath("settings.toml"));
        var result = git.Commit("A", ["../reports/a.md"]);
        Assert.True(result.Committed, result.Error);
        Assert.Equal(["A\treports/a.md"], repo.LastFiles);
    }

    [Fact]
    public void A_folder_that_isnt_a_repository_is_an_error_not_an_exception()
    {
        var plain = Path.Combine(repo.Root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "a.md"), "a");
        var result = new Committer(plain, lines.Add).Commit("A", ["a.md"]);
        Assert.False(result.Committed);
        Assert.Contains("isn't in a git working tree", result.Error);
        Assert.EndsWith("isn't a folder.", new Committer(Path.Combine(repo.Root, "missing"), lines.Add).Commit("A", ["a.md"]).Error);
    }

    [Fact]
    public void Another_gits_index_lock_is_waited_for()
    {
        repo.Write("a.md", "a\n");
        var lockFile = Path.Combine(repo.Path, ".git", "index.lock");
        File.WriteAllText(lockFile, "");
        // The other git finishes once the first try has been refused (whenever that is: a loaded machine is slow).
        var git = new Committer(repo.Path, line =>
        {
            lines.Add(line);
            if (line.Contains("index.lock", StringComparison.Ordinal))
                File.Delete(lockFile);
        }, lockWait: TimeSpan.FromMilliseconds(100));
        var result = git.Commit("A", ["a.md"]);
        Assert.True(result.Committed, result.Error);
        Assert.Contains("git: another git is using the index (index.lock); trying again", lines);
    }

    [Fact]
    public void An_index_lock_that_stays_is_an_error_and_nothing_is_committed()
    {
        repo.Write("a.md", "a\n");
        File.WriteAllText(Path.Combine(repo.Path, ".git", "index.lock"), "");
        var before = repo.Count;
        var result = Git(lockRetries: 2).Commit("A", ["a.md"]);
        File.Delete(Path.Combine(repo.Path, ".git", "index.lock"));
        Assert.False(result.Committed);
        Assert.StartsWith("git add failed: ", result.Error);
        Assert.Contains("index.lock", result.Error);
        Assert.Equal(before, repo.Count);
    }

    [Fact]
    public void A_push_goes_to_the_upstream_and_a_failed_push_keeps_the_commit()
    {
        var bare = Path.Combine(repo.Root, "remote.git");
        TempRepo.GitIn(repo.Root, "init", "-q", "--bare", "-b", "main", bare);
        repo.Git("remote", "add", "origin", bare);
        repo.Git("push", "-q", "-u", "origin", "main");

        repo.Write("a.md", "a\n");
        var pushed = Git().Commit("A", ["a.md"], push: true);
        Assert.True(pushed.Committed && pushed.Pushed, pushed.PushError);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), TempRepo.GitIn(bare, "rev-parse", "main"));
        Assert.Contains("git: pushed", lines);

        repo.Git("remote", "set-url", "origin", Path.Combine(repo.Root, "no-such-remote.git"));
        repo.Write("b.md", "b\n");
        var failed = Git().Commit("B", ["b.md"], push: true);
        Assert.True(failed.Committed);
        Assert.False(failed.Pushed);
        Assert.NotNull(failed.PushError);
        Assert.Null(failed.Error);
        Assert.Equal("B", repo.LastMessage);
        Assert.Contains(lines, l => l.StartsWith("git: push failed: ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_log_never_holds_file_contents_or_messages()
    {
        repo.Write("secret.md", "a personal mobile number\n");
        Git().Commit("Ingest someone's details", ["secret.md"], trailers: ["Co-Authored-By: X <x@example.com>"]);
        var joined = string.Join("\n", lines);
        Assert.DoesNotContain("mobile", joined);
        Assert.DoesNotContain("someone", joined);
    }
}
