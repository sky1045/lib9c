﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Bencodex.Types;
using Libplanet;
using Libplanet.Action;
using Libplanet.Assets;
using Nekoyume.Model.EnumType;
using Nekoyume.Model.Mail;
using Nekoyume.Model.State;
using Nekoyume.TableData;
using Serilog;

namespace Nekoyume.Action
{
    [Serializable]
    [ActionType("buy3")]
    public class Buy3 : GameAction
    {
        public Address buyerAvatarAddress;
        public Address sellerAgentAddress;
        public Address sellerAvatarAddress;
        public Guid productId;
        public Buy.BuyerResult buyerResult;
        public Buy.SellerResult sellerResult;

        protected override IImmutableDictionary<string, IValue> PlainValueInternal => new Dictionary<string, IValue>
        {
            ["buyerAvatarAddress"] = buyerAvatarAddress.Serialize(),
            ["sellerAgentAddress"] = sellerAgentAddress.Serialize(),
            ["sellerAvatarAddress"] = sellerAvatarAddress.Serialize(),
            ["productId"] = productId.Serialize(),
        }.ToImmutableDictionary();

        protected override void LoadPlainValueInternal(IImmutableDictionary<string, IValue> plainValue)
        {
            buyerAvatarAddress = plainValue["buyerAvatarAddress"].ToAddress();
            sellerAgentAddress = plainValue["sellerAgentAddress"].ToAddress();
            sellerAvatarAddress = plainValue["sellerAvatarAddress"].ToAddress();
            productId = plainValue["productId"].ToGuid();
        }

        public override IAccountStateDelta Execute(IActionContext context)
        {
            IActionContext ctx = context;
            var states = ctx.PreviousStates;
            if (ctx.Rehearsal)
            {
                states = states
                    .SetState(buyerAvatarAddress, MarkChanged)
                    .SetState(ctx.Signer, MarkChanged)
                    .SetState(sellerAvatarAddress, MarkChanged)
                    .MarkBalanceChanged(
                        GoldCurrencyMock,
                        ctx.Signer,
                        sellerAgentAddress,
                        GoldCurrencyState.Address);
                return states.SetState(ShopState.Address, MarkChanged);
            }

            if (ctx.Signer.Equals(sellerAgentAddress))
            {
                throw new InvalidAddressException("Aborted as the signer is the seller.");
            }

            var sw = new Stopwatch();
            sw.Start();
            var started = DateTimeOffset.UtcNow;
            Log.Debug("Buy exec started.");

            if (!states.TryGetAvatarState(ctx.Signer, buyerAvatarAddress, out var buyerAvatarState))
            {
                throw new FailedLoadStateException("Aborted as the avatar state of the buyer was failed to load.");
            }
            sw.Stop();
            Log.Debug("Buy Get Buyer AgentAvatarStates: {Elapsed}", sw.Elapsed);
            sw.Restart();

            if (!buyerAvatarState.worldInformation.IsStageCleared(GameConfig.RequireClearedStageLevel.ActionsInShop))
            {
                buyerAvatarState.worldInformation.TryGetLastClearedStageId(out var current);
                throw new NotEnoughClearedStageLevelException(GameConfig.RequireClearedStageLevel.ActionsInShop, current);
            }

            if (!states.TryGetState(ShopState.Address, out Bencodex.Types.Dictionary d))
            {
                throw new FailedLoadStateException("Aborted as the shop state was failed to load.");
            }

            var shopState = new ShopState(d);
            sw.Stop();
            Log.Debug("Buy Get ShopState: {Elapsed}", sw.Elapsed);
            sw.Restart();

            Log.Debug("Execute Buy; buyer: {Buyer} seller: {Seller}", buyerAvatarAddress, sellerAvatarAddress);
            // 상점에서 구매할 아이템을 찾는다.
            if (!shopState.TryGet(sellerAgentAddress, productId, out var shopItem))
            {
                throw new ItemDoesNotExistException(
                    $"Aborted as the shop item ({productId}) was failed to get from the seller agent ({sellerAgentAddress})."
                );
            }
            sw.Stop();
            Log.Debug($"Buy Get Item: {sw.Elapsed}");
            sw.Restart();

            if (!states.TryGetAvatarState(sellerAgentAddress, sellerAvatarAddress, out var sellerAvatarState))
            {
                throw new FailedLoadStateException(
                    $"Aborted as the seller agent/avatar was failed to load from {sellerAgentAddress}/{sellerAvatarAddress}."
                );
            }
            sw.Stop();
            Log.Debug($"Buy Get Seller AgentAvatarStates: {sw.Elapsed}");
            sw.Restart();

            // 돈은 있냐?
            FungibleAssetValue buyerBalance = states.GetBalance(context.Signer, states.GetGoldCurrency());
            if (buyerBalance < shopItem.Price)
            {
                throw new InsufficientBalanceException(
                    ctx.Signer,
                    buyerBalance,
                    $"Aborted as the buyer ({ctx.Signer}) has no sufficient gold: {buyerBalance} < {shopItem.Price}"
                );
            }

            var tax = shopItem.Price.DivRem(100, out _) * Buy.TaxRate;
            var taxedPrice = shopItem.Price - tax;

            // 세금을 송금한다.
            states = states.TransferAsset(
                context.Signer,
                GoldCurrencyState.Address,
                tax);

            // 구매자의 돈을 판매자에게 송금한다.
            states = states.TransferAsset(
                context.Signer,
                sellerAgentAddress,
                taxedPrice
            );

            shopState.Unregister(shopItem);

            // 구매자, 판매자에게 결과 메일 전송
            buyerResult = new Buy.BuyerResult
            {
                shopItem = shopItem,
                itemUsable = shopItem.ItemUsable,
                costume = shopItem.Costume
            };
            var buyerMail = new BuyerMail(buyerResult, ctx.BlockIndex, ctx.Random.GenerateRandomGuid(), ctx.BlockIndex);
            buyerResult.id = buyerMail.id;

            sellerResult = new Buy.SellerResult
            {
                shopItem = shopItem,
                itemUsable = shopItem.ItemUsable,
                costume = shopItem.Costume,
                gold = taxedPrice
            };
            var sellerMail = new SellerMail(sellerResult, ctx.BlockIndex, ctx.Random.GenerateRandomGuid(),
                ctx.BlockIndex);
            sellerResult.id = sellerMail.id;

            buyerAvatarState.Update(buyerMail);
            if (buyerResult.itemUsable != null)
            {
                buyerAvatarState.UpdateFromAddItem(buyerResult.itemUsable, false);
            }

            if (buyerResult.costume != null)
            {
                buyerAvatarState.UpdateFromAddCostume(buyerResult.costume, false);
            }
            sellerAvatarState.Update(sellerMail);

            // 퀘스트 업데이트
            buyerAvatarState.questList.UpdateTradeQuest(TradeType.Buy, shopItem.Price);
            sellerAvatarState.questList.UpdateTradeQuest(TradeType.Sell, shopItem.Price);

            buyerAvatarState.updatedAt = ctx.BlockIndex;
            buyerAvatarState.blockIndex = ctx.BlockIndex;
            sellerAvatarState.updatedAt = ctx.BlockIndex;
            sellerAvatarState.blockIndex = ctx.BlockIndex;

            var materialSheet = states.GetSheet<MaterialItemSheet>();
            buyerAvatarState.UpdateQuestRewards(materialSheet);
            sellerAvatarState.UpdateQuestRewards(materialSheet);

            states = states.SetState(sellerAvatarAddress, sellerAvatarState.Serialize());
            sw.Stop();
            Log.Debug("Buy Set Seller AvatarState: {Elapsed}", sw.Elapsed);
            sw.Restart();

            states = states.SetState(buyerAvatarAddress, buyerAvatarState.Serialize());
            sw.Stop();
            Log.Debug("Buy Set Buyer AvatarState: {Elapsed}", sw.Elapsed);
            sw.Restart();

            states = states.SetState(ShopState.Address, shopState.Serialize());
            sw.Stop();
            var ended = DateTimeOffset.UtcNow;
            Log.Debug("Buy Set ShopState: {Elapsed}", sw.Elapsed);
            Log.Debug("Buy Total Executed Time: {Elapsed}", ended - started);

            return states;
        }
    }
}