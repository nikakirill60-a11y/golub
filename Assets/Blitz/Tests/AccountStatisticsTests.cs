using System;
using BlitzLegacy.Core.Battle;
using BlitzLegacy.Core.Save;
using BlitzLegacy.Runtime.Ui;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace BlitzLegacy.Tests
{
    public sealed class AccountStatisticsTests
    {
        public static AccountData Fixture(int count)
        {
            var account=new AccountData();
            for(var i=0;i<count;i++)
            {
                var result=new BattleResultData{Id="history_"+i,TankId="is-7",MapId="rudniki",FinishedAt=1452931361+i,Outcome=(BattleOutcome)(i%3),CreditsEarned=10000+i,Credits=100,Gold=10,Xp=1000};
                result.Participants.Add(new BattleParticipantResult{IsPlayer=true,Name="Amway921",TankId="is-7",TankName="IS-7",Damage=100+i,Shots=i%2==0?10:2,Hits=i%2==0?1:2,XpRaw=200+i,Xp=1000,Survived=i%2==0,Kills=i%7});account.BattleResults.Add(result);
            }
            account.Rewards["warrior"]=2;account.Rewards["first_victory"]=1;return account;
        }
        [Test] public void Stats_UseHistoryRawXpAndPerBattleHitRatios()
        {
            var acc=Fixture(2);acc.StatBattles=900;var s=AccountBattleStatistics.Calculate(acc);
            Assert.AreEqual(2,s.Battles);Assert.AreEqual(100.5,s.AverageDamage);Assert.AreEqual(.55,s.AverageHitRatio,1e-7);Assert.AreEqual(200.5,s.AverageXp);Assert.AreEqual(201,s.MaxXp);Assert.AreEqual(50,s.WinRate);
        }
        [Test] public void Stats_MatchNativeGodotFixture()
        {
            // Tools/godot_profile_reference.gd -> Logs/profile-reference/native_statistics.json.
            var s=AccountBattleStatistics.Calculate(Fixture(23));
            Assert.AreEqual(1699,s.Rating);Assert.AreEqual(34.7826086956522,s.WinRate,1e-10);
            Assert.AreEqual(111,s.AverageDamage);Assert.AreEqual(211,s.AverageXp);
            Assert.AreEqual(.530434782608696,s.AverageHitRatio,1e-12);Assert.AreEqual(222,s.MaxXp);Assert.AreEqual(6,s.MaxKills);
        }
        [Test] public void Stats_EmptyAccountHasFiniteZeros()
        {var s=AccountBattleStatistics.Calculate(new AccountData());Assert.AreEqual(0,s.Rating);Assert.AreEqual(0,s.AverageXp);Assert.AreEqual(1,AccountBattleStatistics.PageCount(new AccountData()));}
        [Test] public void History_PagesNewestFirstTenAtATime()
        {var a=Fixture(23);Assert.AreEqual(3,AccountBattleStatistics.PageCount(a));Assert.AreEqual("history_22",AccountBattleStatistics.RecentPage(a,1)[0].Id);Assert.AreEqual("history_12",AccountBattleStatistics.RecentPage(a,2)[0].Id);Assert.AreEqual(3,AccountBattleStatistics.RecentPage(a,99).Count);}
        [Test] public void Profile_NativeTabsHistoryPaginationAndReadOnlyResultClick()
        {
            var root=new GameObject("test");AccountStatisticsScreen screen=null;
            try
            {
                var acc=Fixture(23);BattleResultData clicked=null;screen=AccountStatisticsScreen.Create(root.transform,acc,()=>{},r=>clicked=r);
                Assert.AreEqual(0,screen.ActiveTab);Assert.AreEqual("23",screen.SourceUi.Text("tabs/summary/GridContainer/grid_elem/battles").text);Assert.AreEqual(10,screen.VisibleResultCount);
                screen.SourceUi.Button("btn_battles").onClick.Invoke();Assert.AreEqual(1,screen.ActiveTab);
                Canvas.ForceUpdateCanvases();Assert.Greater(screen.SourceUi.Nodes["tabs/battles/ScrollContainer"].rect.height,100);
                Assert.Greater(screen.SourceUi.Nodes["tabs/battles/bresults_controls/HBoxContainer/folders"].anchoredPosition.x,60);
                screen.ShowPage(3);Assert.AreEqual(3,screen.VisibleResultCount);Assert.IsFalse(screen.SourceUi.Button("tabs/battles/bresults_controls/HBoxContainer/btn_next").interactable);
                var rows=screen.SourceUi.Nodes["tabs/battles/ScrollContainer/battles_box"];
                rows.GetChild(0).Find("btn").GetComponent<Button>().onClick.Invoke();Assert.AreEqual("history_2",clicked.Id);Assert.AreEqual(23,acc.BattleResults.Count);Assert.AreEqual(AccountData.DefaultCredits,acc.Credits);
                screen.ShowTab(2);Assert.AreEqual(2,screen.ActiveTab);screen.ShowTab(3);Assert.AreEqual(0,screen.SourceUi.Nodes["tabs/tanks"].childCount);
                Canvas.ForceUpdateCanvases();Assert.AreEqual(117.49f,screen.SourceUi.Nodes["btn_summary"].rect.width,.1f);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void MedalPopup_UsesRegistryTitleDescriptionAndGold()
        {
            var root=new GameObject("test");try {var popup=MedalInfoPopup.Open(root.transform,"warrior");Assert.AreEqual("warrior",popup.RewardId);Assert.AreEqual(150,MedalInfoPopup.Definition("warrior")["gold"].AsInt());Assert.IsNotNull(popup.GetComponentInChildren<Canvas>());}finally{UnityEngine.Object.DestroyImmediate(root);}
        }
    }
}
