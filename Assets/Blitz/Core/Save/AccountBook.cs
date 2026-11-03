using System;
using System.Collections.Generic;
using BlitzLegacy.Core.Serialization;

namespace BlitzLegacy.Core.Save
{
    public enum AccountCreateStatus
    {
        Created,
        EmptyFields,
        LoginUsed,
    }

    public readonly struct AccountCreateResult
    {
        public AccountCreateResult(AccountCreateStatus status, AccountData account, string errorKey)
        {
            Status = status;
            Account = account;
            ErrorLocalizationKey = errorKey;
        }

        public AccountCreateStatus Status { get; }

        public AccountData Account { get; }

        /// <summary>Localization key, never a user-facing literal.</summary>
        public string ErrorLocalizationKey { get; }

        public bool Ok
        {
            get { return Status == AccountCreateStatus.Created; }
        }
    }

    /// <summary>Port of Godot <c>SaveMain</c> (assets/data/save_main.gd).</summary>
    public sealed class AccountBook
    {
        public const string ErrorKeyEmptyFields = "LOGIN_CREATE/empty_fields";
        public const string ErrorKeyLoginUsed = "LOGIN_CREATE/login_used";

        /// <summary>Tanks Godot grants to a brand new account.</summary>
        public static readonly string[] StarterTanks =
        {
            "t1_cunningham",
            "ltraktor",
            "gb01_medium_mark_i",
            "ms-1",
        };

        private readonly Dictionary<string, AccountData> _accounts =
            new Dictionary<string, AccountData>(StringComparer.Ordinal);

        public string CurrentAccountKey { get; set; } = string.Empty;

        /// <summary>
        /// Set by <see cref="LogOut"/>, cleared by the next sign-in: until
        /// then the boot opens the login screen even with SKIP LOGIN SCREEN
        /// on.
        /// </summary>
        public bool LoggedOut { get; set; }

        public IReadOnlyDictionary<string, AccountData> Accounts
        {
            get { return _accounts; }
        }

        public int Count
        {
            get { return _accounts.Count; }
        }

        public bool Has(string key)
        {
            return key != null && _accounts.ContainsKey(key);
        }

        public void Put(AccountData account)
        {
            if (account == null)
            {
                throw new ArgumentNullException("account");
            }

            _accounts[account.Key] = account;
        }

        public bool LoginUsed(string login)
        {
            foreach (var key in _accounts.Keys)
            {
                var separator = key.IndexOf(':');
                var storedLogin = separator < 0 ? key : key.Substring(0, separator);
                if (string.Equals(storedLogin, login, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public AccountCreateResult CreateAccount(
            string name,
            string login,
            string password,
            double nowUnixSeconds)
        {
            if (string.IsNullOrEmpty(name) ||
                string.IsNullOrEmpty(login) ||
                string.IsNullOrEmpty(password))
            {
                return new AccountCreateResult(
                    AccountCreateStatus.EmptyFields, null, ErrorKeyEmptyFields);
            }

            if (LoginUsed(login))
            {
                return new AccountCreateResult(
                    AccountCreateStatus.LoginUsed, null, ErrorKeyLoginUsed);
            }

            var account = new AccountData
            {
                Name = name,
                Login = login,
                Password = password,
                CreationTime = nowUnixSeconds,
                LastLogin = nowUnixSeconds,
            };

            for (var i = 0; i < StarterTanks.Length; i++)
            {
                account.AddTank(StarterTanks[i], true);
            }

            _accounts[account.Key] = account;
            return new AccountCreateResult(AccountCreateStatus.Created, account, null);
        }

        public bool SwitchAccount(string key)
        {
            if (!Has(key))
            {
                return false;
            }

            CurrentAccountKey = key;
            return true;
        }

        public AccountData GetCurrentAccount()
        {
            AccountData account;
            return _accounts.TryGetValue(CurrentAccountKey ?? string.Empty, out account)
                ? account
                : null;
        }

        public bool LogInToCurrentAccount(double nowUnixSeconds)
        {
            var account = GetCurrentAccount();
            if (account == null)
            {
                return false;
            }

            account.LogIn(nowUnixSeconds);
            LoggedOut = false;
            return true;
        }

        /// <summary>
        /// The hangar's LOGOUT. The session ends, while the account, its
        /// login and password stay, and so does the choice of the current
        /// one, which the login screen offers to play again.
        /// </summary>
        public void LogOut()
        {
            LoggedOut = true;
        }

        /// <summary>Login by credentials, as the login tab does.</summary>
        public AccountData FindByCredentials(string login, string password)
        {
            AccountData account;
            return _accounts.TryGetValue(AccountData.MakeKey(login, password), out account)
                ? account
                : null;
        }

        public bool DeleteAccount(string key)
        {
            if (!Has(key))
            {
                return false;
            }

            _accounts.Remove(key);
            if (string.Equals(CurrentAccountKey, key, StringComparison.Ordinal))
            {
                CurrentAccountKey = string.Empty;
            }

            return true;
        }

        /// <summary>Serializes only the index; accounts are stored one file each.</summary>
        public JsonNode IndexToJson()
        {
            return JsonNode.NewObject()
                .Set("current_acc", CurrentAccountKey ?? string.Empty)
                .Set("logged_out", LoggedOut);
        }

        public void IndexFromJson(JsonNode node)
        {
            CurrentAccountKey = node == null ? string.Empty : node["current_acc"].AsString();
            LoggedOut = node != null && node["logged_out"].AsBool();
        }
    }
}
