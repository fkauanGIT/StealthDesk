using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace StealthDesk.Web.Server.Users;

/// <summary>Random passwords that meet the server's password rules, for an administrator to hand over once.</summary>
public static class TemporaryPasswords
{
  // Characters that are easy to tell apart when read aloud or copied by hand.
  private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
  private const string Lower = "abcdefghijkmnopqrstuvwxyz";
  private const string Digits = "23456789";
  private const string Symbols = "!@#$%*-_=+?";

  public static string Generate(PasswordOptions rules)
  {
    var required = new List<string>();
    if (rules.RequireUppercase)
    {
      required.Add(Upper);
    }

    if (rules.RequireLowercase)
    {
      required.Add(Lower);
    }

    if (rules.RequireDigit)
    {
      required.Add(Digits);
    }

    if (rules.RequireNonAlphanumeric)
    {
      required.Add(Symbols);
    }

    var pool = required.Count > 0 ? string.Concat(required) : Lower + Upper + Digits;
    var length = Math.Max(Math.Max(rules.RequiredLength, required.Count), rules.RequiredUniqueChars);

    // One of each required kind first, so the rules always hold, then the rest from all of them.
    var characters = required.Select(Pick).ToList();
    while (characters.Count < length || characters.Distinct().Count() < rules.RequiredUniqueChars)
    {
      characters.Add(Pick(pool));
    }

    RandomNumberGenerator.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(characters));
    return new string([.. characters]);
  }

  private static char Pick(string from) => from[RandomNumberGenerator.GetInt32(from.Length)];
}
