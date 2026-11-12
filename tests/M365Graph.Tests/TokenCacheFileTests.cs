using System.Text;
using System.Text.Json;

namespace M365Graph.Tests;

public class TokenCacheFileTests : Temp
{
    private string CachePath => Path.Combine(Folder, "sub", "msal-cache.json");

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes!);

    [Fact]
    public void No_file_is_no_cache()
    {
        Assert.Null(TokenCacheFile.Read(CachePath));
    }

    [Fact]
    public void A_good_file_is_read_as_it_is()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        var bytes = Encoding.UTF8.GetBytes("{\r\n    \"Account\": {}\r\n}\r\n");
        File.WriteAllBytes(CachePath, bytes);
        Assert.Equal(bytes, TokenCacheFile.Read(CachePath));
    }

    /// <summary>10 Nov 2026: a shorter write over a longer one left two stray characters after the JSON.</summary>
    [Theory]
    [InlineData("{\"a\": {\"b\": 1}}\"}")]
    [InlineData("{\"a\": {\"b\": 1}}}\n")]
    [InlineData("  {\"a\": {\"b\": 1}} {\"c\": 2}")]
    [InlineData("﻿{\"a\": {\"b\": 1}}")]
    public void A_damaged_file_keeps_its_first_complete_json_value(string damaged)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, damaged, new UTF8Encoding(false));
        using var salvaged = JsonDocument.Parse(TokenCacheFile.Read(CachePath)!);
        Assert.Equal(1, salvaged.RootElement.GetProperty("a").GetProperty("b").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"a\": ")]
    [InlineData("not json")]
    public void A_file_with_no_complete_json_is_an_error(string bad)
    {
        Assert.Throws<InvalidDataException>(() => TokenCacheFile.Salvage(Encoding.UTF8.GetBytes(bad)));
    }

    [Fact]
    public void A_write_makes_the_folder_replaces_the_file_and_leaves_no_temporary_file()
    {
        Assert.True(TokenCacheFile.Write(CachePath, "{\"v\": 1}"u8.ToArray()));
        Assert.True(TokenCacheFile.Write(CachePath, "{\"v\": 2}"u8.ToArray()));
        Assert.Equal("{\"v\": 2}", Text(TokenCacheFile.Read(CachePath)));
        Assert.Equal(["msal-cache.json"], Directory.GetFiles(Path.GetDirectoryName(CachePath)!).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_held_open_by_another_program_is_retried_until_it_is_free()
    {
        TokenCacheFile.Write(CachePath, "{\"v\": 1}"u8.ToArray());
        var held = new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var waits = new List<TimeSpan>();

        var written = TokenCacheFile.Write(CachePath, "{\"v\": 2}"u8.ToArray(), wait =>
        {
            waits.Add(wait);
            if (waits.Count == 2)
                held.Dispose();   // the other program lets go
        });

        Assert.True(written);
        Assert.Equal([TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.2)], waits);
        Assert.Equal("{\"v\": 2}", Text(TokenCacheFile.Read(CachePath)));
    }

    [Fact]
    public void A_file_that_stays_locked_is_not_written_and_the_temporary_file_goes()
    {
        TokenCacheFile.Write(CachePath, "{\"v\": 1}"u8.ToArray());
        var waits = 0;
        using (new FileStream(CachePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(TokenCacheFile.Write(CachePath, "{\"v\": 2}"u8.ToArray(), _ => waits++));
        Assert.Equal(9, waits);   // ten tries
        Assert.Equal("{\"v\": 1}", Text(TokenCacheFile.Read(CachePath)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(CachePath)!));
    }

    [Fact]
    public void A_read_waits_out_a_brief_lock_too()
    {
        TokenCacheFile.Write(CachePath, "{\"v\": 1}"u8.ToArray());
        var held = new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var data = TokenCacheFile.Read(CachePath, _ => held.Dispose());
        Assert.Equal("{\"v\": 1}", Text(data));
    }
}
