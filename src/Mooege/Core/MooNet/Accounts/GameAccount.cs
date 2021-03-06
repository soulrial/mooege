﻿/*
 * Copyright (C) 2011 mooege project
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA
 */

using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using Mooege.Common.Storage;
using Mooege.Common.Helpers.Hash;
using Mooege.Core.Cryptography;
using Mooege.Core.MooNet.Friends;
using Mooege.Core.MooNet.Helpers;
using Mooege.Core.MooNet.Objects;
using Mooege.Core.MooNet.Toons;
using Mooege.Core.MooNet.Channels;
using Mooege.Net.MooNet;

namespace Mooege.Core.MooNet.Accounts
{
    public class GameAccount : PersistentRPCObject
    {
        public Account Owner { get; set; }

        public D3.OnlineService.EntityId D3GameAccountId { get; private set; }

        public FieldKeyHelper.Program Program { get; private set; }

        public D3.Account.BannerConfiguration BannerConfiguration { get; set; }
        public D3.GameMessage.SetGameAccountSettings Settings { get; set; }

        /// <summary>
        /// Away status
        /// </summary>
        public AwayStatusFlag AwayStatus { get; private set; }

        private D3.OnlineService.EntityId _lastPlayedHeroId = AccountHasNoToons;
        public D3.OnlineService.EntityId lastPlayedHeroId
        {
            get
            {
                if (_lastPlayedHeroId == AccountHasNoToons && Toons.Count > 0)
                    _lastPlayedHeroId = this.Toons.First().Value.D3EntityID;
                return _lastPlayedHeroId;
            }
            set
            {
                _lastPlayedHeroId = value;
            }
        }

        public List<bnet.protocol.achievements.AchievementUpdateRecord> Achievements { get; set; }
        public List<bnet.protocol.achievements.CriteriaUpdateRecord> AchievementCriteria { get; set; }

        public D3.Profile.AccountProfile Profile
        {
            get
            {
                return D3.Profile.AccountProfile.CreateBuilder()
                    .Build();
            }
        }

        private static readonly D3.OnlineService.EntityId AccountHasNoToons =
            D3.OnlineService.EntityId.CreateBuilder().SetIdHigh(0).SetIdLow(0).Build();

        public Dictionary<ulong, Toon> Toons
        {
            get { return ToonManager.GetToonsForGameAccount(this); }
        }

        private static ulong? _persistentIdCounter = null;

        protected override ulong GenerateNewPersistentId()
        {
            if (_persistentIdCounter == null)
                _persistentIdCounter = AccountManager.GetNextAvailablePersistentId();

            return (ulong)++_persistentIdCounter;
        }

        public GameAccount(ulong persistentId, ulong accountId)
            : base(persistentId)
        {
            this.SetField(AccountManager.GetAccountByPersistentID(accountId));
        }

        public GameAccount(Account account)
            : base(account.BnetEntityId.Low)
        {
            this.SetField(account);
        }

        private void SetField(Account owner)
        {
            this.Owner = owner;
            var bnetGameAccountHigh = ((ulong)EntityIdHelper.HighIdType.GameAccountId) + (0x6200004433);
            this.BnetEntityId = bnet.protocol.EntityId.CreateBuilder().SetHigh(bnetGameAccountHigh).SetLow(this.PersistentID).Build();
            this.D3GameAccountId = D3.OnlineService.EntityId.CreateBuilder().SetIdHigh(bnetGameAccountHigh).SetIdLow(this.PersistentID).Build();

            //TODO: Now hardcode all game accounts to D3
            this.Program = FieldKeyHelper.Program.D3;
            this.BannerConfiguration = D3.Account.BannerConfiguration.CreateBuilder()
                .SetBannerShape(2952440006)
                .SetSigilMain(976722430)
                .SetSigilAccent(803826460)
                .SetPatternColor(1797588777)
                .SetBackgroundColor(1379006192)
                .SetSigilColor(1797588777)
                .SetSigilPlacement(3057352154)
                .SetPattern(4173846786)
                .SetUseSigilVariant(true)
                .SetEpicBanner(0)
                .Build();

            this.Achievements = new List<bnet.protocol.achievements.AchievementUpdateRecord>();
            this.AchievementCriteria = new List<bnet.protocol.achievements.CriteriaUpdateRecord>();

        }

        public bool IsOnline { get { return this.LoggedInClient != null; } }

        private MooNetClient _loggedInClient;

        public MooNetClient LoggedInClient
        {
            get
            {
                return this._loggedInClient;
            }
            set
            {
                this._loggedInClient = value;

                // notify friends.
                if (FriendManager.Friends[this.Owner.BnetEntityId.Low].Count == 0) return; // if account has no friends just skip.

                var fieldKey = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 1, 2, 0);
                var field = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetBoolValue(this.IsOnline).Build()).Build();
                var operation = bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field).Build();

                var state = bnet.protocol.presence.ChannelState.CreateBuilder().SetEntityId(this.Owner.BnetEntityId).AddFieldOperation(operation).Build();
                var channelState = bnet.protocol.channel.ChannelState.CreateBuilder().SetExtension(bnet.protocol.presence.ChannelState.Presence, state);
                var notification = bnet.protocol.channel.UpdateChannelStateNotification.CreateBuilder().SetStateChange(channelState).Build();

                foreach (var friend in FriendManager.Friends[this.Owner.BnetEntityId.Low])
                {
                    var account = AccountManager.GetAccountByPersistentID(friend.Id.Low);
                    if (account == null || account.IsOnline == null) return; // only send to friends that are online.

                    // make the rpc call.
                    var d3GameAccounts = GameAccountManager.GetGameAccountsForAccountProgram(account, FieldKeyHelper.Program.D3);
                    foreach (var d3GameAccount in d3GameAccounts.Values)
                    {
                        if (d3GameAccount.IsOnline)
                        {
                            d3GameAccount.LoggedInClient.MakeTargetedRPC(this, () =>
                                bnet.protocol.channel.ChannelSubscriber.CreateStub(d3GameAccount.LoggedInClient).NotifyUpdateChannelState(null, notification, callback => { }));
                        }
                    }
                }
            }
        }

        public D3.Account.Digest Digest
        {
            get
            {
                var builder = D3.Account.Digest.CreateBuilder().SetVersion(102) // 7447=>99, 7728=> 100, 8801=>102
                    .SetBannerConfiguration(this.BannerConfiguration)
                    .SetFlags(0)
                    .SetLastPlayedHeroId(lastPlayedHeroId);

                return builder.Build();
            }
        }

//        protected override void NotifySubscriptionAdded(MooNetClient client)
        public override List<bnet.protocol.presence.FieldOperation> GetSubscriptionNotifications()
        {
            var operationList = new List<bnet.protocol.presence.FieldOperation>();

            //gameaccount
            //D3,2,1,0 -> D3.Account.BannerConfiguration
            //D3,2,2,0 -> ToonId
            //D3,3,1,0 -> Hero Class
            //D3,3,2,0 -> Hero's current level
            //D3,3,3,0 -> D3.Hero.VisualEquipment
            //D3,3,4,0 -> Hero's flags
            //D3,3,5,0 -> Hero Name
            //Bnet,2,1,0 -> true
            //Bnet,2,4,0 -> FourCC = "D3"
            //Bnet,2,5,0 -> Unk Int
            //Bnet,2,6,0 -> BattleTag
            //Bnet,2,7,0 -> accountlow#1

            // Banner configuration
            var fieldKey1 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 2, 1, 0);
            var field1 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey1).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(this.BannerConfiguration.ToByteString()).Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field1).Build());

            if (this.lastPlayedHeroId != AccountHasNoToons)
            {
                var toon = ToonManager.GetToonByLowID(this.lastPlayedHeroId.IdLow);

                //ToonId
                var fieldKey7 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 2, 2, 0);
                var field7 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey7).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(toon.D3EntityID.ToByteString()).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field7).Build());

                //Hero Class
                var fieldKey8 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 3, 1, 0);
                var field8 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey8).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(toon.ClassID).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field8).Build());

                //Hero Level
                var fieldKey9 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 3, 2, 0);
                var field9 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey9).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(toon.Level).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field9).Build());

                //D3.Hero.VisualEquipment
                var fieldKey10 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 3, 3, 0);
                var field10 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey10).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(toon.Equipment.ToByteString()).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field10).Build());

                //Hero Flags
                var fieldKey11 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 3, 4, 0);
                var field11 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey11).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue((int)(toon.Flags | ToonFlags.AllUnknowns)).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field11).Build());

                //Hero Name
                var fieldKey12 = FieldKeyHelper.Create(FieldKeyHelper.Program.D3, 3, 5, 0);
                var field12 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey12).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(toon.Name).Build()).Build();
                operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field12).Build());
            }


            // ??
            var fieldKey2 = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 2, 1, 0);
            var field2 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey2).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetBoolValue(true).Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field2).Build());

            // Program - FourCC "D3"
            var fieldKey3 = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 2, 4, 0);
            var field3 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey3).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetFourccValue("D3").Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field3).Build());

            // Unknown int
            var fieldKey4 = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 2, 5, 0);
            var field4 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey4).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(1324923597904795).Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field4).Build());

            //BattleTag
            var fieldKey5 = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 2, 6, 0);
            var field5 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey5).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(this.Owner.BattleTag).Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field5).Build());

            //Account.Low + "#1"
            var fieldKey6 = FieldKeyHelper.Create(FieldKeyHelper.Program.BNet, 2, 7, 0);
            var field6 = bnet.protocol.presence.Field.CreateBuilder().SetKey(fieldKey6).SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(Owner.BnetEntityId.Low.ToString() + "#1").Build()).Build();
            operationList.Add(bnet.protocol.presence.FieldOperation.CreateBuilder().SetField(field6).Build());

            return operationList;

            //// Create a presence.ChannelState
            //var state = bnet.protocol.presence.ChannelState.CreateBuilder().SetEntityId(this.BnetGameAccountID).AddRangeFieldOperation(operations).Build();

            //// Embed in channel.ChannelState
            //var channelState = bnet.protocol.channel.ChannelState.CreateBuilder().SetExtension(bnet.protocol.presence.ChannelState.Presence, state);

            //// Put in addnotification message
            //var notification = bnet.protocol.channel.AddNotification.CreateBuilder().SetChannelState(channelState);

            //// Make the rpc call
            //client.MakeTargetedRPC(this, () =>
            //    bnet.protocol.channel.ChannelSubscriber.CreateStub(client).NotifyAdd(null, notification.Build(), callback => { }));
        }

        public void Update(bnet.protocol.presence.FieldOperation operation)
        {
            switch (operation.Operation)
            {
                case bnet.protocol.presence.FieldOperation.Types.OperationType.SET:
                    DoSet(operation.Field);
                    break;
                case bnet.protocol.presence.FieldOperation.Types.OperationType.CLEAR:
                    DoClear(operation.Field);
                    break;
            }
        }

        private void DoSet(bnet.protocol.presence.Field field)
        {
            switch ((FieldKeyHelper.Program)field.Key.Program)
            {
                case FieldKeyHelper.Program.D3:
                    if (field.Key.Group == 4 && field.Key.Field == 1)
                    {
                        if (field.Value.HasMessageValue) //7727 Sends empty SET instead of a CLEAR -Egris
                        {
                            var entityId = D3.OnlineService.EntityId.ParseFrom(field.Value.MessageValue);
                            var channel = ChannelManager.GetChannelByEntityId(entityId);

                            this.LoggedInClient.CurrentChannel = channel;
                        }
                        else
                        {
                            Logger.Warn("Emtpy-field: {0}, {1}, {2}", field.Key.Program, field.Key.Group, field.Key.Field);
                        }
                    }
                    else if (field.Key.Group == 4 && field.Key.Field == 2)
                    {
                        //catch to stop Logger.Warn spam on client start and exit
                        // should D3.4.2 int64 Current screen (0=in-menus, 1=in-menus, 3=in-menus); see ScreenStatus sent to ChannelService.UpdateChannelState call /raist
                    }
                    else if (field.Key.Group == 4 && field.Key.Field == 3)
                    {
                        //Looks to be the ToonFlags of the party leader/inviter when it is an int, OR the message set in an open to friends game when it is a string /dustinconrad
                    }
                    else
                    {
                        Logger.Warn("Unknown set-field: {0}, {1}, {2} := {3}", field.Key.Program, field.Key.Group, field.Key.Field, field.Value);
                    }
                    break;
                case FieldKeyHelper.Program.BNet:
                    if (field.Key.Group == 2 && field.Key.Field == 3) // Away status
                    {
                        this.AwayStatus = (AwayStatusFlag)field.Value.IntValue;
                    }
                    else
                    {
                        Logger.Warn("Unknown set-field: {0}, {1}, {2} := {3}", field.Key.Program, field.Key.Group, field.Key.Field, field.Value);
                    }
                    break;
            }
        }

        private void DoClear(bnet.protocol.presence.Field field)
        {
            switch ((FieldKeyHelper.Program)field.Key.Program)
            {
                case FieldKeyHelper.Program.D3:
                    Logger.Warn("Unknown clear-field: {0}, {1}, {2}", field.Key.Program, field.Key.Group, field.Key.Field);
                    break;
                case FieldKeyHelper.Program.BNet:
                    Logger.Warn("Unknown clear-field: {0}, {1}, {2}", field.Key.Program, field.Key.Group, field.Key.Field);
                    break;
            }
        }

        public bnet.protocol.presence.Field QueryField(bnet.protocol.presence.FieldKey queryKey)
        {
            var field = bnet.protocol.presence.Field.CreateBuilder().SetKey(queryKey);

            switch ((FieldKeyHelper.Program)queryKey.Program)
            {
                case FieldKeyHelper.Program.D3:
                    if (queryKey.Group == 2 && queryKey.Field == 1) // Banner configuration
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(this.BannerConfiguration.ToByteString()).Build());
                    }
                    else if (queryKey.Group == 3 && queryKey.Field == 1) // Hero's class (GbidClass)
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(this.LoggedInClient.CurrentToon.ClassID).Build());
                    }
                    else if (queryKey.Group == 3 && queryKey.Field == 2) // Hero's current level
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(this.LoggedInClient.CurrentToon.Level).Build());
                    }
                    else if (queryKey.Group == 3 && queryKey.Field == 3) // Hero's visible equipment
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(this.LoggedInClient.CurrentToon.Equipment.ToByteString()).Build());
                    }
                    else if (queryKey.Group == 3 && queryKey.Field == 4) // Hero's flags (gender and such)
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue((uint)(this.LoggedInClient.CurrentToon.Flags | ToonFlags.AllUnknowns)).Build());
                    }
                    else if (queryKey.Group == 3 && queryKey.Field == 5) // Toon name
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(this.LoggedInClient.CurrentToon.Name).Build());
                    }
                    else if (queryKey.Group == 4 && queryKey.Field == 1) // Channel ID if the client is online
                    {
                        if (this.LoggedInClient != null && this.LoggedInClient.CurrentChannel != null) field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetMessageValue(this.LoggedInClient.CurrentChannel.D3EntityId.ToByteString()).Build());
                        else field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().Build());
                    }
                    else if (queryKey.Group == 4 && queryKey.Field == 2) // Current screen (all known values are just "in-menu"; also see ScreenStatuses sent in ChannelService.UpdateChannelState)
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetIntValue(0).Build());
                    }
                    else
                    {
                        Logger.Warn("Unknown query-key: {0}, {1}, {2}", queryKey.Program, queryKey.Group, queryKey.Field);
                    }
                    break;
                case FieldKeyHelper.Program.BNet:
                    if (queryKey.Group == 2 && queryKey.Field == 4) // Program - always D3
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetFourccValue("D3").Build());
                    }
                    else if (queryKey.Group == 2 && queryKey.Field == 6) // BattleTag
                    {
                        field.SetValue(bnet.protocol.attribute.Variant.CreateBuilder().SetStringValue(this.Owner.BattleTag).Build());
                    }
                    else
                    {
                        Logger.Warn("Unknown query-key: {0}, {1}, {2}", queryKey.Program, queryKey.Group, queryKey.Field);
                    }
                    break;
            }

            return field.HasValue ? field.Build() : null;
        }

        public override string ToString()
        {
            return String.Format("{{ GameAccount: {0} [lowId: {1}] }}", this.Owner.BattleTag, this.BnetEntityId.Low);
        }

        public void SaveToDB()
        {
            try
            {
                if (ExistsInDB())
                {
                    var query =
                        string.Format(
                            "UPDATE gameaccount SET accountId={0} WHERE id={1}",
                            this.Owner.PersistentID, this.PersistentID);

                    var cmd = new SQLiteCommand(query, DBManager.Connection);
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    var query =
                        string.Format(
                            "INSERT INTO gameaccount (id, accountId) VALUES({0},{1})",
                            this.PersistentID, this.Owner.PersistentID);

                    var cmd = new SQLiteCommand(query, DBManager.Connection);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, "GameAccount.SaveToDB()");
            }
        }

        public bool DeleteFromDB()
        {
            try
            {
                // Remove from DB
                if (!ExistsInDB()) return false;

                var query = string.Format("DELETE FROM gameaccount WHERE id={0}", this.PersistentID);
                var cmd = new SQLiteCommand(query, DBManager.Connection);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception e)
            {
                Logger.ErrorException(e, "GameAccount.DeleteFromDB()");
                return false;
            }
        }

        private bool ExistsInDB()
        {
            var query =
                string.Format(
                    "SELECT id from gameaccounts where id={0}",
                    this.PersistentID);

            var cmd = new SQLiteCommand(query, DBManager.Connection);
            var reader = cmd.ExecuteReader();
            return reader.HasRows;
        }

        //TODO: figure out what 1 and 3 represent, or if it is a flag since all observed values are powers of 2 so far /dustinconrad
        public enum AwayStatusFlag : uint
        {
            Available = 0x00,
            UnknownStatus1 = 0x01,
            Away = 0x02,
            UnknownStatus2 = 0x03,
            Busy = 0x04
        }
    }
}
