using Eule.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server;

/// <summary>
/// Account management from the server's terminal (Coolify → Terminal), since there is no sign-up yet:
/// <code>
///   dotnet Eule.Server.dll user add ali@example.com --name "Ali"
///   dotnet Eule.Server.dll user passwd ali@example.com
///   dotnet Eule.Server.dll user list
///   dotnet Eule.Server.dll user disable ali@example.com   (enable: user enable …)
/// </code>
/// Passwords are asked for interactively and never appear in the command line or shell history.
/// With --password-stdin they are read from standard input instead (for scripts).
/// </summary>
public static class UserCli
{
    public const int MinPasswordLength = 8;

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        string cmd = args.Length > 0 ? args[0] : "";
        string email = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "";
        bool fromStdin = args.Contains("--password-stdin");

        switch (cmd)
        {
            case "add":
            {
                if (!email.Contains('@')) return Fail("Kullanım: user add <e-posta> [--name \"Ad\"] [--password-stdin]");
                if (await db.Users.AnyAsync(u => u.Email == email)) return Fail($"{email} zaten var.");
                string? password = ReadPassword(fromStdin);
                if (password == null) return 1;
                var user = new User { Email = email, DisplayName = Option(args, "--name") ?? email.Split('@')[0], CreatedAt = now };
                user.PasswordHash = hasher.HashPassword(user, password);
                db.Users.Add(user);
                await db.SaveChangesAsync();
                Console.WriteLine($"Hesap açıldı: {user.Email} ({user.DisplayName})");
                return 0;
            }
            case "passwd":
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null) return Fail($"{email} bulunamadı.");
                string? password = ReadPassword(fromStdin);
                if (password == null) return 1;
                user.PasswordHash = hasher.HashPassword(user, password);
                // A new password signs out every device.
                await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
                await db.SaveChangesAsync();
                Console.WriteLine($"Şifre değişti, tüm oturumlar kapatıldı: {user.Email}");
                return 0;
            }
            case "disable":
            case "enable":
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null) return Fail($"{email} bulunamadı.");
                user.IsActive = cmd == "enable";
                if (!user.IsActive)
                    await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
                await db.SaveChangesAsync();
                Console.WriteLine($"{user.Email}: {(user.IsActive ? "etkin" : "devre dışı")}");
                return 0;
            }
            case "list":
            {
                foreach (var u in await db.Users.OrderBy(u => u.Email).ToListAsync())
                {
                    int notes = await db.Notes.CountAsync(n => n.UserId == u.Id && n.DeletedAt == null);
                    Console.WriteLine($"{u.Email,-36} {u.DisplayName,-20} {(u.IsActive ? "etkin" : "kapalı"),-7} " +
                                      $"not: {notes,-5} son giriş: {u.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "—"}");
                }
                return 0;
            }
            default:
                return Fail("Komutlar: user add | passwd | list | disable | enable");
        }
    }

    static string? ReadPassword(bool fromStdin)
    {
        string? first, second;
        if (fromStdin || Console.IsInputRedirected)
        {
            // Input piped from Windows can carry a "\r" line ending and a byte-order mark; neither is part of the password.
            first = second = Console.ReadLine()?.TrimStart('﻿').TrimEnd('\r', '\n');
        }
        else
        {
            first = Prompt("Şifre: ");
            second = Prompt("Şifre (tekrar): ");
        }
        if (string.IsNullOrEmpty(first) || first.Length < MinPasswordLength)
        {
            Fail($"Şifre en az {MinPasswordLength} karakter olmalı.");
            return null;
        }
        if (first != second)
        {
            Fail("Şifreler aynı değil.");
            return null;
        }
        return first;
    }

    static string Prompt(string label)
    {
        Console.Write(label);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray());
    }

    static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
