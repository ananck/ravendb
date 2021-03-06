﻿using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Raven.Server.Utils
{
    internal static class NameUtils
    {
        public static List<string> AllowedResourceNameCharacters = new List<string>()
        {
            "_", @"\-", @"\."
        };

        public static List<string> AllowedIndexNameCharacters = new List<string>()
        {
            "_", @"\/", @"\-", @"\."
        };

        public static string ValidResourceNameCharacters = $"([{string.Join("", AllowedResourceNameCharacters)}]+)";
        public static string ValidIndexNameCharacters = $"([{string.Join("", AllowedIndexNameCharacters)}]+)";

        private static readonly Regex ValidResourceNameCharactersRegex = new Regex(ValidResourceNameCharacters, RegexOptions.Compiled);
        private static readonly Regex ValidIndexNameCharactersRegex = new Regex(ValidIndexNameCharacters, RegexOptions.Compiled);
        private static readonly Regex NameStartsOrEndsWithDotOrContainsConsecutiveDotsRegex = new Regex(@"^\.|\.\.|\.$", RegexOptions.Compiled);

        public static bool IsValidResourceName(string name)
        {
            return IsValidName(name, ValidResourceNameCharactersRegex);
        }
        
        public static bool IsValidIndexName(string name)
        {
            return IsValidName(name, ValidIndexNameCharactersRegex);
        }
        
        public static bool IsDotCharSurroundedByOtherChars(string name)
        {
            return NameStartsOrEndsWithDotOrContainsConsecutiveDotsRegex.IsMatch(name) == false;
        }
        
        private static bool IsValidName(string name, Regex regex)
        {
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) == false && regex.Matches(c.ToString()).Count == 0)
                {
                    return false;
                }
            }
            
            return true;
        }
    }
    
    public class NameValidation
    {
        public bool IsValid { get; set; } 
        public string ErrorMessage { get; set; }
    }
}
