﻿using System;
using System.Linq;
using System.Net;
using LykkeApi2.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common;
using Core.Blockchain;
using Core.Constants;
using Core.Repositories;
using Core.Services;
using Lykke.Common.ApiLibrary.Exceptions;
using Lykke.Cqrs;
using Lykke.Job.HistoryExportBuilder.Contract;
using Lykke.Job.HistoryExportBuilder.Contract.Commands;
using Lykke.Service.Assets.Client.Models;
using Lykke.Service.ClientAccount.Client;
using Lykke.Service.ClientAccount.Client.Models;
using Lykke.Service.ClientAccount.Client.Models.Response.Wallets;
using Lykke.Service.History.Client;
using Lykke.Service.History.Contracts.Enums;
using LykkeApi2.Models.Blockchain;
using Swashbuckle.AspNetCore.Annotations;
using LykkeApi2.Models.History;
using ErrorResponse = LykkeApi2.Models.ErrorResponse;

namespace LykkeApi2.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class HistoryController : Controller
    {
        private readonly IRequestContext _requestContext;
        private readonly IClientAccountClient _clientAccountService;
        private readonly ICqrsEngine _cqrsEngine;
        private readonly IHistoryExportsRepository _historyExportsRepository;
        private readonly IHistoryClient _historyClient;
        private readonly IAssetsHelper _assetsHelper;
        private readonly IBlockchainExplorersProvider _blockchainExplorersProvider;

        public HistoryController(
            IRequestContext requestContext,
            IClientAccountClient clientAccountService,
            ICqrsEngine cqrsEngine,
            IHistoryExportsRepository historyExportsRepository,
            IHistoryClient historyClient,
            IAssetsHelper assetsHelper,
            IBlockchainExplorersProvider blockchainExplorersProvider)
        {
            _requestContext = requestContext ?? throw new ArgumentNullException(nameof(requestContext));
            _clientAccountService = clientAccountService ?? throw new ArgumentNullException(nameof(clientAccountService));
            _cqrsEngine = cqrsEngine;
            _historyExportsRepository = historyExportsRepository;
            _historyClient = historyClient;
            _assetsHelper = assetsHelper;
            _blockchainExplorersProvider = blockchainExplorersProvider;
        }

        [HttpPost("client/csv")]
        [SwaggerOperation("RequestClientHistoryCsv")]
        [ProducesResponseType(typeof(RequestClientHistoryCsvResponseModel), (int)HttpStatusCode.OK)]
        public IActionResult RequestClientHistoryCsv([FromBody]RequestClientHistoryCsvRequestModel model)
        {
            var id = Guid.NewGuid().ToString();

            _cqrsEngine.SendCommand(new ExportClientHistoryCommand
            {
                Id = id,
                ClientId = _requestContext.ClientId,
                OperationTypes = model.OperationType,
                AssetId = model.AssetId,
                AssetPairId = model.AssetPairId
            }, null, HistoryExportBuilderBoundedContext.Name);

            return Ok(new RequestClientHistoryCsvResponseModel { Id = id });
        }

        [HttpGet("client/csv")]
        [SwaggerOperation("GetClientHistoryCsv")]
        [ProducesResponseType(typeof(GetClientHistoryCsvResponseModel), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetClientHistoryCsv([FromQuery]string id)
        {
            return Ok(new GetClientHistoryCsvResponseModel { Url = await _historyExportsRepository.GetUrl(_requestContext.ClientId, id) });
        }

        /// <summary>
        /// Getting history by wallet identifier
        /// </summary>
        /// <param name="walletId">Wallet identifier</param>
        /// <param name="operationType">The type of the operation, possible values: CashIn, CashOut, Trade, OrderEvent</param>
        /// <param name="assetId">Asset identifier</param>
        /// <param name="assetPairId">Asset pair identifier</param>
        /// <param name="take">How many maximum items have to be returned</param>
        /// <param name="skip">How many items skip before returning</param>
        /// <returns></returns>
        [HttpGet("wallet/{walletId}")]
        [SwaggerOperation("GetByWalletId")]
        [ProducesResponseType(typeof(ErrorResponse), (int)HttpStatusCode.InternalServerError)]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.NotFound)]
        [ProducesResponseType(typeof(IEnumerable<HistoryResponseModel>), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetByWalletId(
            string walletId,
            [FromQuery(Name = "operationType")] string[] operationType,
            [FromQuery] string assetId,
            [FromQuery] string assetPairId,
            [FromQuery] int take,
            [FromQuery] int skip)
        {
            var clientId = _requestContext.ClientId;

            // TODO: should be removed after release. operationType parameter should be of type HistoryType[]
            var types = new HashSet<HistoryType>();
            foreach (var opType in operationType)
            {
                if (Enum.TryParse<HistoryType>(opType, out var result))
                    types.Add(result);
            }

            var wallet = await _clientAccountService.Wallets.GetWalletAsync(walletId);

            if (wallet == null || wallet.ClientId != clientId)
                return NotFound();

            // TODO: remove after migration to wallet id
            if (wallet.Type == WalletType.Trading)
                walletId = clientId;

            var data = await _historyClient.HistoryApi.GetHistoryByWalletAsync(Guid.Parse(walletId), types.ToArray(),
                assetId, assetPairId, offset: skip, limit: take);

            return Ok(data.SelectMany(x => x.ToResponseModel()));
        }

        /// <summary>
        /// Getting history by wallet identifier
        /// </summary>
        /// <param name="walletId">Wallet identifier</param>
        /// <param name="assetPairId">Asset pair identifier</param>
        /// <param name="take">How many maximum items have to be returned</param>
        /// <param name="skip">How many items skip before returning</param>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <param name="tradeType"></param>
        /// <returns></returns>
        [HttpGet("{walletId}/trades")]
        [SwaggerOperation("GetTradesByWalletId")]
        [ProducesResponseType(typeof(ErrorResponse), (int)HttpStatusCode.InternalServerError)]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.NotFound)]
        [ProducesResponseType(typeof(IEnumerable<TradeResponseModel>), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetTradesByWalletId(
            string walletId,
            [FromQuery] string assetPairId,
            [FromQuery] int take,
            [FromQuery] int skip,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] TradeType? tradeType = null)
        {
            if (from >= to)
                throw LykkeApiErrorException.BadRequest(LykkeApiErrorCodes.Service.InvalidInput, "fromDt value should be less than toDt value");

            var clientId = _requestContext.ClientId;

            var wallet = await _clientAccountService.Wallets.GetWalletAsync(walletId);

            if (wallet == null || wallet.ClientId != clientId)
                return NotFound();

            // TODO: remove after migration to wallet id
            if (wallet.Type == WalletType.Trading)
                walletId = clientId;

            var data = await _historyClient.TradesApi.GetTradesByWalletAsync(Guid.Parse(walletId),
                assetPairId: assetPairId, offset: skip, limit: take, tradeType: tradeType, from: from, to: to);

            var result = await data.SelectAsync(x => x.ToTradeResponseModel(_assetsHelper));

            return Ok(result.OrderByDescending(x => x.Timestamp));
        }

        /// <summary>
        /// Getting history by wallet identifier
        /// </summary>
        /// <param name="walletId">Wallet identifier</param>
        /// <param name="operation"></param>
        /// <param name="assetId">Asset identifier</param>
        /// <param name="take">How many maximum items have to be returned</param>
        /// <param name="skip">How many items skip before returning</param>
        /// <param name="from"></param>
        /// <param name="to"></param>
        /// <returns></returns>
        [HttpGet("{walletId}/funds")]
        [SwaggerOperation("GetFundsByWalletId")]
        [ProducesResponseType(typeof(ErrorResponse), (int)HttpStatusCode.InternalServerError)]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.NotFound)]
        [ProducesResponseType(typeof(IEnumerable<FundsResponseModel>), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetFundsByWalletId(
            string walletId,
            [FromQuery(Name = "operation")] FundsOperation[] operation,
            [FromQuery] string assetId,
            [FromQuery] int take,
            [FromQuery] int skip,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            if (from >= to)
                throw LykkeApiErrorException.BadRequest(LykkeApiErrorCodes.Service.InvalidInput, "fromDt value should be less than toDt value");

            var clientId = _requestContext.ClientId;

            var wallet = await _clientAccountService.Wallets.GetWalletAsync(walletId);

            if (wallet == null || wallet.ClientId != clientId)
                return NotFound();

            // TODO: remove after migration to wallet id
            if (wallet.Type == WalletType.Trading)
                walletId = clientId;

            if (operation.Length == 0)
                operation = Enum.GetValues(typeof(FundsOperation)).Cast<FundsOperation>().ToArray();

            var data = await _historyClient.HistoryApi.GetHistoryByWalletAsync(Guid.Parse(walletId), operation.Select(x => x.ToHistoryType()).ToArray(),
                assetId: assetId, offset: skip, limit: take, from: from, to: to);

            var result = await data.SelectAsync(x => x.ToFundsResponseModel(_assetsHelper));

            return Ok(result.OrderByDescending(x => x.Timestamp));
        }

        /// <summary>
        /// Getting explorer url links for the given blockchain type and txHash
        /// </summary>
        /// <param name="blockchainType">Wallet identifier</param>
        /// <param name="transactionHash"></param>
        /// <returns></returns>
        [HttpGet("crypto/{assetId}/transactions/{transactionHash}/links")]
        [SwaggerOperation("GetFundsByWalletId")]
        [ProducesResponseType(typeof(ErrorResponse), (int)HttpStatusCode.InternalServerError)]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.NotFound)]
        [ProducesResponseType(typeof(BlockchainExplorersCollection), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetExplorerLinks(
            [FromRoute] string assetId,
            [FromRoute] string transactionHash)
        {
            if (string.IsNullOrEmpty(assetId))
                throw LykkeApiErrorException.BadRequest(LykkeApiErrorCodes.Service.InvalidInput, $"{nameof(assetId)} should not null");

            if (string.IsNullOrEmpty(transactionHash))
                throw LykkeApiErrorException.BadRequest(LykkeApiErrorCodes.Service.InvalidInput, "transactionHash should not null");

            var asset = await _assetsHelper.GetAssetAsync(assetId);

            if (asset == null)
                throw LykkeApiErrorException.BadRequest(LykkeApiErrorCodes.Service.InvalidInput, $"{nameof(assetId)} should exist");

            string blockchainType = null;

            if (!string.IsNullOrEmpty(asset.BlockchainIntegrationLayerId))
            {
                blockchainType = asset.BlockchainIntegrationLayerId;
            }
            else if (asset.Type == AssetType.Erc20Token)
            {
                //HARDCODE
                blockchainType = "Ethereum";
            }
            else
            {
                return NoContent();
            }

            var explorersLink = await _blockchainExplorersProvider.GetAsync(blockchainType, transactionHash);
            var response = new BlockchainExplorersCollection()
            {
                Links = explorersLink?.Select(x => new BlockchainExplorerLinkResponse()
                {
                    Name = x.Name,
                    Url = x.ExplorerUrlTemplateFormatted
                })
            };

            return Ok(response);
        }
    }
}
