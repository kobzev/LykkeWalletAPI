﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Lykke.Service.Balances.AutorestClient.Models;
using Lykke.Service.Balances.Client;
using Lykke.Service.ClientAccount.Client.AutorestClient;
using Lykke.Service.ClientAccount.Client.AutorestClient.Models;
using Lykke.Service.HftInternalService.Client.AutorestClient;
using LykkeApi2.Infrastructure;
using LykkeApi2.Models.ApiKey;
using LykkeApi2.Models.ClientBalancesModels;
using LykkeApi2.Models.ResponceModels;
using LykkeApi2.Models.Wallets;
using LykkeApi2.Strings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.SwaggerGen.Annotations;
using ClientBalanceResponseModel = LykkeApi2.Models.ClientBalancesModels.ClientBalanceResponseModel;
using CreateWalletRequest = LykkeApi2.Models.Wallets.CreateWalletRequest;

namespace LykkeApi2.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    public class WalletsController : Controller
    {
        private readonly IRequestContext _requestContext;
        private readonly IBalancesClient _balancesClient;
        private readonly IClientAccountService _clientAccountService;
        private readonly IHftInternalServiceAPI _hftInternalService;

        public WalletsController(IRequestContext requestContext, IClientAccountService clientAccountService, IBalancesClient balancesClient, IHftInternalServiceAPI hftInternalService)
        {
            _requestContext = requestContext ?? throw new ArgumentNullException(nameof(requestContext));
            _clientAccountService = clientAccountService ?? throw new ArgumentNullException(nameof(clientAccountService));
            _balancesClient = balancesClient ?? throw new ArgumentNullException(nameof(balancesClient));
            _hftInternalService = hftInternalService ?? throw new ArgumentNullException(nameof(hftInternalService));
        }

        /// <summary>
        /// Create wallet.
        /// </summary>
        [HttpPost]
        [SwaggerOperation("CreateWallet")]
        public async Task<WalletModel> CreateWallet([FromBody] CreateWalletRequest request)
        {
            var wallet = await _clientAccountService.CreateWalletAsync(
                new Lykke.Service.ClientAccount.Client.AutorestClient.Models.CreateWalletRequest(_requestContext.ClientId, request.Type, request.Name));

            return new WalletModel { Id = wallet.Id, Name = wallet.Name, Type = wallet.Type };
        }

        /// <summary>
        /// Create trusted wallet and generate API-key.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        [HttpPost("hft")]
        [ProducesResponseType(typeof(CreateApiKeyResponse), (int)HttpStatusCode.OK)]
        [SwaggerOperation("CreateApiWallet")]
        public async Task<IActionResult> CreateApiWallet([FromBody] CreateApiKeyRequest request)
        {
            var apiKey = await _hftInternalService.CreateKeyAsync(
                new Lykke.Service.HftInternalService.Client.AutorestClient.Models.CreateApiKeyRequest(_requestContext.ClientId, request.Name));

            if (apiKey == null)
                return BadRequest();

            return Ok(new CreateApiKeyResponse { ApiKey = apiKey.Key, WalletId = apiKey.Wallet });
        }

        /// <summary>
        /// Delete wallet.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpDelete("{id}")]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(void), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> DeleteWallet(string id)
        {
            // checking if user owns the specified wallet
            var wallets = await _clientAccountService.GetWalletsByClientIdAsync(_requestContext.ClientId);
            var wallet = wallets?.FirstOrDefault(x => x.Id == id);
            if (wallet == null)
                return NotFound();

            // checking if this is HFT wallet
            // todo: always delete wallet through ClientAccountService; HFT internal service should process deleted messages by itself
            var clientKeys = await _hftInternalService.GetKeysAsync(_requestContext.ClientId);
            if (clientKeys.Any(x => x.Wallet == id))
            {
                await _hftInternalService.DeleteKeyAsync(id);
                return Ok();
            }
            await _clientAccountService.DeleteWalletAsync(id);
            return Ok();
        }

        /// <summary>
        /// Get all client wallets.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(IEnumerable<WalletDto>), (int)HttpStatusCode.OK)]
        [ProducesResponseType((int)HttpStatusCode.NotFound)]
        [SwaggerOperation("GetWallets")]
        public async Task<IActionResult> GetWallets()
        {
            var wallets = await _clientAccountService.GetWalletsByClientIdAsync(_requestContext.ClientId);

            if (wallets == null)
                return NotFound();

            return Ok(wallets.Select(wallet => new WalletModel { Id = wallet.Id, Name = wallet.Name, Type = wallet.Type }));
        }

        /// <summary>
        /// Get specified wallet.
        /// </summary>
        [HttpGet("{id}")]
        [SwaggerOperation("GetWallet")]
        [ProducesResponseType(typeof(WalletModel), (int)HttpStatusCode.OK)]
        public async Task<IActionResult> GetWallet(string id)
        {
            var wallet = await _clientAccountService.GetWalletAsync(id);

            if (wallet == null)
                return NotFound();

            return Ok(new WalletModel { Id = wallet.Id, Name = wallet.Name, Type = wallet.Type });
        }


        /// <summary>
        /// Get all wallets balances.
        /// </summary>
        [Authorize]
        [HttpGet("balances")]
        [SwaggerOperation("GetBalances")]
        [ProducesResponseType(typeof(IEnumerable<WalletBalancesModel>), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetBalances()
        {
            var result = new List<WalletBalancesModel>();
            var clientBalances = await _balancesClient.GetClientBalances(_requestContext.ClientId);
            result.Add(new WalletBalancesModel
            {
                Id = _requestContext.ClientId,
                Type = "Trading",
                Balances = clientBalances?.Select(ClientBalanceResponseModel.Create) ?? new ClientBalanceResponseModel[0]
            });

            var wallets = await _clientAccountService.GetWalletsByClientIdAsync(_requestContext.ClientId);
            if (wallets != null)
            {
                foreach (var wallet in wallets)
                {
                    var balances = await _balancesClient.GetClientBalances(wallet.Id);
                    result.Add(new WalletBalancesModel
                    {
                        Id = wallet.Id,
                        Type = wallet.Type,
                        Name = wallet.Name,
                        Balances = balances?.Select(ClientBalanceResponseModel.Create) ?? new ClientBalanceResponseModel[0]
                    });
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// Get trading wallet balances.
        /// </summary>
        [Authorize]
        [HttpGet("trading/balances")]
        [SwaggerOperation("GetTradingWalletBalances")]
        [ProducesResponseType(typeof(IEnumerable<ClientBalanceResponseModel>), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetTradingWalletBalances()
        {
            var clientBalances = await _balancesClient.GetClientBalances(_requestContext.ClientId);

            return Ok(clientBalances?.Select(ClientBalanceResponseModel.Create) ?? new ClientBalanceResponseModel[0]);
        }

        /// <summary>
        /// Get specified wallet balances.
        /// </summary>
        [Authorize]
        [HttpGet("{walletId}/balances")]
        [SwaggerOperation("GetWalletBalances")]
        [ProducesResponseType(typeof(IEnumerable<ClientBalanceResponseModel>), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetWalletBalances(string walletId)
        {
            var clientBalances = await _balancesClient.GetClientBalances(walletId);
            if (clientBalances == null)
            {
                var wallet = await _clientAccountService.GetWalletAsync(walletId);
                if (wallet == null)
                    return NotFound(new ApiResponse(HttpStatusCode.NotFound, Phrases.ClientBalanceNotFound));
            }

            return Ok(clientBalances?.Select(ClientBalanceResponseModel.Create) ?? new ClientBalanceResponseModel[0]);
        }

        /// <summary>
        /// Get balances by asset id.
        /// </summary>
        [Authorize]
        [HttpGet("balances/{assetId}")]
        [SwaggerOperation("GetBalancesByAssetId")]
        [ProducesResponseType(typeof(IEnumerable<WalletAssetBalanceModel>), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetBalancesByAssetId(string assetId)
        {
            var result = new List<WalletAssetBalanceModel>();
            var clientBalance = await _balancesClient.GetClientBalanceByAssetId(
                new ClientBalanceByAssetIdModel
                {
                    ClientId = _requestContext.ClientId,
                    AssetId = assetId
                });

            if (!string.IsNullOrEmpty(clientBalance?.ErrorMessage))
            {
                return NotFound(new ApiResponse(HttpStatusCode.NotFound, clientBalance.ErrorMessage ?? Phrases.ClientBalanceNotFound));
            }

            result.Add(new WalletAssetBalanceModel
            {
                Id = _requestContext.ClientId,
                Type = "Trading",
                Balances = clientBalance != null ? ClientBalanceResponseModel.Create(clientBalance) : null
            });

            var wallets = await _clientAccountService.GetWalletsByClientIdAsync(_requestContext.ClientId);
            if (wallets != null)
            {
                foreach (var wallet in wallets)
                {
                    var balance = await _balancesClient.GetClientBalanceByAssetId(
                        new ClientBalanceByAssetIdModel
                        {
                            ClientId = wallet.Id,
                            AssetId = assetId
                        });
                    result.Add(new WalletAssetBalanceModel
                    {
                        Id = wallet.Id,
                        Type = wallet.Type,
                        Name = wallet.Name,
                        Balances = balance != null ? ClientBalanceResponseModel.Create(balance) : null
                    });
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// Get trading wallet balances by asset id.
        /// </summary>
        [Authorize]
        [HttpGet("trading/balances/{assetId}")]
        [SwaggerOperation("GetTradindWalletBalanceByAssetId")]
        [ProducesResponseType(typeof(ClientBalanceResponseModel), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetTradindWalletBalanceByAssetId(string assetId)
        {
            var clientBalanceResult = await _balancesClient.GetClientBalanceByAssetId(
                new ClientBalanceByAssetIdModel
                {
                    ClientId = _requestContext.ClientId,
                    AssetId = assetId
                });

            if (clientBalanceResult != null && string.IsNullOrEmpty(clientBalanceResult.ErrorMessage))
            {
                return Ok(ClientBalanceResponseModel.Create(clientBalanceResult));
            }

            return NotFound(new ApiResponse(HttpStatusCode.NotFound,
                clientBalanceResult?.ErrorMessage ?? Phrases.ClientBalanceNotFound));
        }

        /// <summary>
        /// Get specified wallet balances by asset id.
        /// </summary>
        [Authorize]
        [HttpGet("{walletId}/balances/{assetId}")]
        [SwaggerOperation("GetWalletBalanceByAssetId")]
        [ProducesResponseType(typeof(ClientBalanceResponseModel), (int)HttpStatusCode.OK)]
        [ProducesResponseType(typeof(ApiResponse), (int)HttpStatusCode.NotFound)]
        public async Task<IActionResult> GetWalletBalanceByAssetId(string walletId, string assetId)
        {
            var clientBalanceResult = await _balancesClient.GetClientBalanceByAssetId(
                new ClientBalanceByAssetIdModel
                {
                    ClientId = walletId,
                    AssetId = assetId
                });

            if (clientBalanceResult != null && string.IsNullOrEmpty(clientBalanceResult.ErrorMessage))
            {
                return Ok(ClientBalanceResponseModel.Create(clientBalanceResult));
            }

            return NotFound(new ApiResponse(HttpStatusCode.NotFound,
                clientBalanceResult?.ErrorMessage ?? Phrases.ClientBalanceNotFound));
        }
    }
}