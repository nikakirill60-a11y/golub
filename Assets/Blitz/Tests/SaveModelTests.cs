using BlitzLegacy.Core.Save;
using BlitzLegacy.Core.Serialization;
using NUnit.Framework;

namespace BlitzLegacy.Tests
{
    public sealed class SaveModelTests
    {
        private const double Now = 1757000000d;

        private static AccountBook BookWithOneAccount(out AccountData account)
        {
            var book = new AccountBook();
            var result = book.CreateAccount("Sasha", "login1", "pass1", Now);
            Assert.IsTrue(result.Ok, "account creation should succeed");
            account = result.Account;
            return book;
        }

        [Test]
        public void CreateAccount_UsesGodotKeyFormatAndDefaults()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);

            Assert.AreEqual("login1:pass1", account.Key);
            Assert.IsTrue(book.Has("login1:pass1"));
            Assert.AreEqual(AccountData.DefaultCredits, account.Credits);
            Assert.AreEqual(AccountData.DefaultGold, account.Gold);
            Assert.AreEqual(AccountData.DefaultBlacklistSlotsMap, account.BlacklistSlotsMap);
            Assert.AreEqual(AccountData.DefaultBlacklistSlotsTank, account.BlacklistSlotsTank);
        }

        [Test]
        public void CreateAccount_GrantsTheFourStarterTanks()
        {
            AccountData account;
            BookWithOneAccount(out account);

            Assert.AreEqual(AccountBook.StarterTanks.Length, account.Tanks.Count);
            foreach (var name in AccountBook.StarterTanks)
            {
                Assert.IsTrue(account.HasTank(name), "missing starter tank " + name);
            }

            Assert.AreEqual(AccountBook.StarterTanks.Length, account.TanksBoughtCount);
        }

        [Test]
        public void CreateAccount_RejectsEmptyFieldsAndDuplicateLogin()
        {
            var book = new AccountBook();

            var empty = book.CreateAccount(string.Empty, "login1", "pass1", Now);
            Assert.AreEqual(AccountCreateStatus.EmptyFields, empty.Status);
            Assert.AreEqual(AccountBook.ErrorKeyEmptyFields, empty.ErrorLocalizationKey);

            Assert.IsTrue(book.CreateAccount("Sasha", "login1", "pass1", Now).Ok);

            var duplicate = book.CreateAccount("Other", "login1", "other", Now);
            Assert.AreEqual(AccountCreateStatus.LoginUsed, duplicate.Status);
            Assert.AreEqual(AccountBook.ErrorKeyLoginUsed, duplicate.ErrorLocalizationKey);
            Assert.AreEqual(1, book.Count);
        }

        [Test]
        public void LoginUsed_ChecksOnlyTheLoginHalfOfTheKey()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);

            Assert.IsTrue(book.LoginUsed("login1"));
            Assert.IsFalse(book.LoginUsed("pass1"));
            Assert.IsFalse(book.LoginUsed("login1:pass1"));
        }

        [Test]
        public void FindByCredentials_MatchesTheLoginTabBehaviour()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);

            Assert.AreSame(account, book.FindByCredentials("login1", "pass1"));
            Assert.IsNull(book.FindByCredentials("login1", "wrong"));
        }

        [Test]
        public void SwitchAndLogIn_StampsLastLogin()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);

            Assert.IsFalse(book.SwitchAccount("nope:nope"));
            Assert.IsTrue(book.SwitchAccount(account.Key));
            Assert.AreSame(account, book.GetCurrentAccount());

            Assert.IsTrue(book.LogInToCurrentAccount(Now + 60d));
            Assert.AreEqual(Now + 60d, account.LastLogin);
            Assert.IsTrue(account.LoggedIn);
        }

        [Test]
        public void LogOut_KeepsTheAccountUntilTheNextSignIn()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);
            book.SwitchAccount(account.Key);
            book.LogInToCurrentAccount(Now);

            book.LogOut();
            Assert.IsTrue(book.LoggedOut);
            Assert.AreEqual(account.Key, book.CurrentAccountKey, "Logout forgot the current account");
            Assert.AreSame(account, book.FindByCredentials("login1", "pass1"));
            Assert.AreEqual(1, book.Count);

            book.SwitchAccount(account.Key);
            Assert.IsTrue(book.LoggedOut, "Choosing an account is not a sign-in");
            Assert.IsTrue(book.LogInToCurrentAccount(Now + 60d));
            Assert.IsFalse(book.LoggedOut);
        }

        [Test]
        public void DeleteAccount_ClearsCurrentSelection()
        {
            AccountData account;
            var book = BookWithOneAccount(out account);
            book.SwitchAccount(account.Key);

            Assert.IsTrue(book.DeleteAccount(account.Key));
            Assert.AreEqual(0, book.Count);
            Assert.AreEqual(string.Empty, book.CurrentAccountKey);
            Assert.IsNull(book.GetCurrentAccount());
        }

        [Test]
        public void AddTank_UpgradesResearchedToBoughtWithoutDuplicating()
        {
            var account = new AccountData();
            account.AddTank("ms-1");
            Assert.AreEqual(1, account.Tanks.Count);
            Assert.IsFalse(account.HasTank("ms-1"));
            Assert.IsTrue(account.HasTank("ms-1", true));

            account.AddTank("ms-1", true);
            Assert.AreEqual(1, account.Tanks.Count);
            Assert.IsTrue(account.HasTank("ms-1"));

            Assert.IsNull(account.AddTank(TankSaveData.DefaultName, true));
        }

        [Test]
        public void AccountRoundTrip_PreservesEveryPortedField()
        {
            AccountData account;
            BookWithOneAccount(out account);

            account.Credits = 1234;
            account.Gold = 7;
            account.FreeXp = 999;
            account.SelectedTankName = "ms-1";
            account.SelectedTankId = 2;
            account.KnyazGiven = true;
            account.TimeIngame = 4321.5d;
            account.StatBattles = 40;
            account.StatWins = 30;
            account.ResearchedModules.Add("gun_x");
            account.Rewards["beta_tester"] = 1;
            account.AchievementsDone.Add("first_blood");
            account.ShopGuaranteedRewards["crate"] = 3;

            var tank = account.GetTank("ms-1");
            tank.Exp = 555;
            tank.RadioId = 2;
            tank.ShellsAmounts[1] = 12;
            tank.Equipment.Add("rammer");
            tank.Rewards["skin"] = 2;

            var restored = AccountData.FromJson(JsonNode.Parse(account.ToJson().ToJson()));

            Assert.AreEqual(account.Key, restored.Key);
            Assert.AreEqual(1234, restored.Credits);
            Assert.AreEqual(7, restored.Gold);
            Assert.AreEqual(999, restored.FreeXp);
            Assert.AreEqual("ms-1", restored.SelectedTankName);
            Assert.AreEqual(2, restored.SelectedTankId);
            Assert.IsTrue(restored.KnyazGiven);
            Assert.AreEqual(4321.5d, restored.TimeIngame, 0.0001d);
            Assert.AreEqual(75f, restored.WinRatePercent, 0.0001f);
            Assert.Contains("gun_x", restored.ResearchedModules);
            Assert.AreEqual(1, restored.Rewards["beta_tester"]);
            Assert.Contains("first_blood", restored.AchievementsDone);
            Assert.AreEqual(3, restored.ShopGuaranteedRewards["crate"]);

            var restoredTank = restored.GetTank("ms-1");
            Assert.IsNotNull(restoredTank);
            Assert.AreEqual(555, restoredTank.Exp);
            Assert.AreEqual(2, restoredTank.RadioId);
            Assert.AreEqual(12, restoredTank.ShellsAmounts[1]);
            Assert.AreEqual(TankSaveData.ShellSlotCount, restoredTank.ShellsAmounts.Count);
            Assert.Contains("rammer", restoredTank.Equipment);
            Assert.AreEqual(2, restoredTank.Rewards["skin"]);
        }

        [Test]
        public void AccountRoundTrip_KeepsSixtyFourBitCurrenciesExact()
        {
            var account = new AccountData
            {
                Credits = long.MaxValue,
                Gold = 5000000000L,
                // 2^53 + 1: a double-backed JSON number would round it.
                FreeXp = 9007199254740993L,
            };

            var json = account.ToJson().ToJson();
            StringAssert.Contains("9223372036854775807", json);
            StringAssert.Contains("9007199254740993", json);

            var loaded = AccountData.FromJson(JsonNode.Parse(json));
            Assert.AreEqual(long.MaxValue, loaded.Credits);
            Assert.AreEqual(5000000000L, loaded.Gold);
            Assert.AreEqual(9007199254740993L, loaded.FreeXp);
        }

        [Test]
        public void JsonNumbers_KeepIntegersExactAndFractionsAsDoubles()
        {
            var node = JsonNode.Parse(
                "{\"min\": -9223372036854775808, \"half\": 2.5, \"exp\": 1e3, \"huge\": 1e30}");
            Assert.AreEqual(long.MinValue, node["min"].AsLong());
            Assert.AreEqual(2.5d, node["half"].AsDouble());
            Assert.AreEqual(1000L, node["exp"].AsLong());
            Assert.AreEqual(long.MaxValue, node["huge"].AsLong());
            StringAssert.Contains("-9223372036854775808", node.ToJson());
            StringAssert.Contains("2.5", node.ToJson());
        }

        [Test]
        public void AddCurrency_SaturatesInsteadOfWrapping()
        {
            Assert.AreEqual(3000000000L, AccountData.AddCurrency(int.MaxValue, 852516353L));
            Assert.AreEqual(long.MaxValue, AccountData.AddCurrency(long.MaxValue - 5, 10));
            Assert.AreEqual(long.MinValue, AccountData.AddCurrency(long.MinValue + 5, -10));
            Assert.AreEqual(-7L, AccountData.AddCurrency(3, -10));
        }

        [Test]
        public void SaveService_RoundTripsThroughStorageLikeGodotLayout()
        {
            var storage = new MemorySaveStorage();
            var service = new SaveService(storage);

            var book = new AccountBook();
            var first = book.CreateAccount("Sasha", "login1", "pass1", Now).Account;
            book.CreateAccount("Petr", "login2", "pass2", Now);
            book.SwitchAccount(first.Key);
            service.SaveBook(book);

            Assert.IsTrue(storage.Exists(SaveService.MainPath));
            Assert.IsTrue(storage.Exists(SaveService.AccountPath(first)));

            var reloaded = service.LoadBook();
            Assert.AreEqual(2, reloaded.Count);
            Assert.AreEqual(first.Key, reloaded.CurrentAccountKey);
            Assert.IsNotNull(reloaded.GetCurrentAccount());
            Assert.AreEqual("Sasha", reloaded.GetCurrentAccount().Name);
        }

        [Test]
        public void SaveService_RemembersTheLogoutUntilTheNextSignIn()
        {
            var storage = new MemorySaveStorage();
            var service = new SaveService(storage);
            AccountData account;
            var book = BookWithOneAccount(out account);
            book.SwitchAccount(account.Key);
            book.LogOut();
            service.SaveBook(book);

            var reloaded = service.LoadBook();
            Assert.IsTrue(reloaded.LoggedOut);
            Assert.AreEqual(account.Key, reloaded.CurrentAccountKey);

            reloaded.LogInToCurrentAccount(Now);
            service.SaveBook(reloaded);
            Assert.IsFalse(service.LoadBook().LoggedOut);

            // An index written before logout existed reads as signed in.
            storage.WriteAllText(SaveService.MainPath,
                JsonNode.NewObject().Set("current_acc", account.Key).ToJson());
            Assert.IsFalse(service.LoadBook().LoggedOut);
        }

        [Test]
        public void SaveService_RemovesFilesOfDeletedAccounts()
        {
            var storage = new MemorySaveStorage();
            var service = new SaveService(storage);

            var book = new AccountBook();
            var doomed = book.CreateAccount("Sasha", "login1", "pass1", Now).Account;
            book.CreateAccount("Petr", "login2", "pass2", Now);
            service.SaveBook(book);

            book.DeleteAccount(doomed.Key);
            service.SaveBook(book);

            Assert.IsFalse(storage.Exists(SaveService.AccountPath(doomed)));
            Assert.AreEqual(1, service.LoadBook().Count);
        }

        [Test]
        public void SaveService_DropsCurrentKeyWhenAccountIsMissing()
        {
            var storage = new MemorySaveStorage();
            storage.WriteAllText(
                SaveService.MainPath,
                JsonNode.NewObject().Set("current_acc", "ghost:ghost").ToJson());

            Assert.AreEqual(string.Empty, new SaveService(storage).LoadBook().CurrentAccountKey);
        }

        [Test]
        public void SaveService_SurvivesCorruptFiles()
        {
            var storage = new MemorySaveStorage();
            storage.WriteAllText(SaveService.MainPath, "{ this is not json");
            storage.WriteAllText(SaveService.AccountsDirectory + "/broken.json", "nope");

            var book = new SaveService(storage).LoadBook();
            Assert.AreEqual(0, book.Count);
            Assert.AreEqual(string.Empty, book.CurrentAccountKey);
        }

        [Test]
        public void Settings_RoundTripKeepsGodotAudioDefaults()
        {
            var storage = new MemorySaveStorage();
            var service = new SaveService(storage);

            var defaults = service.LoadSettings();
            Assert.AreEqual(SettingsData.DefaultVolumeMusic, defaults.VolumeMusic, 0.0001f);
            Assert.AreEqual(SettingsData.DefaultVolumeUi, defaults.VolumeUi, 0.0001f);
            Assert.AreEqual(SettingsData.DefaultVolumeSfx, defaults.VolumeSfx, 0.0001f);

            defaults.Language = "ru";
            defaults.VolumeMusic = 0.9f;
            service.SaveSettings(defaults);

            var reloaded = service.LoadSettings();
            Assert.AreEqual("ru", reloaded.Language);
            Assert.AreEqual(0.9f, reloaded.VolumeMusic, 0.0001f);
        }
    }
}
