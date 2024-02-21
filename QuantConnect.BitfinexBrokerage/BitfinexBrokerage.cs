/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using Newtonsoft.Json;
using QuantConnect.Brokerages.Bitfinex.Messages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using QuantConnect.Api;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using Order = QuantConnect.Orders.Order;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.Bitfinex
{
    /// <summary>
    /// Bitfinex Brokerage implementation
    /// </summary>
    [BrokerageFactory(typeof(BitfinexBrokerageFactory))]
    public partial class BitfinexBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private bool _loggedSupportsOnlyTradeBars;
        private readonly SymbolPropertiesDatabaseSymbolMapper _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(Market.Bitfinex);

        #region IBrokerage

        /// <summary>
        /// Checks if the websocket connection is connected or in the process of connecting
        /// </summary>
        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            var parameters = new JsonObject
            {
                { "symbol", _symbolMapper.GetBrokerageSymbol(order.Symbol) },
                { "amount", order.Quantity.ToStringInvariant() },
                { "type", ConvertOrderType(_algorithm.BrokerageModel.AccountType, order.Type) },
                { "price", GetOrderPrice(order).ToStringInvariant() }
            };

            var orderProperties = order.Properties as BitfinexOrderProperties;
            if (orderProperties != null)
            {
                if (order.Type == OrderType.Limit)
                {
                    var flags = 0;
                    if (orderProperties.Hidden) flags |= OrderFlags.Hidden;
                    if (orderProperties.PostOnly) flags |= OrderFlags.PostOnly;

                    parameters.Add("flags", flags);
                }
            }

            var clientOrderId = GetNextClientOrderId();
            parameters.Add("cid", clientOrderId);

            _orderMap.TryAdd(clientOrderId, order);

            var obj = new JsonArray { 0, "on", null, parameters };
            var json = JsonConvert.SerializeObject(obj);
            WebSocket.Send(json);

            return true;
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            if (order.BrokerId.Count == 0)
            {
                throw new ArgumentNullException(nameof(order.BrokerId), "BitfinexBrokerage.UpdateOrder: There is no brokerage id to be updated for this order.");
            }

            if (order.BrokerId.Count > 1)
            {
                throw new NotSupportedException("BitfinexBrokerage.UpdateOrder: Multiple orders update not supported. Please cancel and re-create.");
            }

            var parameters = new JsonObject
            {
                { "id", Parse.Long(order.BrokerId.First()) },
                { "amount", order.Quantity.ToStringInvariant() },
                { "price", GetOrderPrice(order).ToStringInvariant() }
            };

            var obj = new JsonArray { 0, "ou", null, parameters };
            var json = JsonConvert.SerializeObject(obj);
            WebSocket.Send(json);

            return true;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was submitted for cancellation, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            Log.Trace("BitfinexBrokerage.CancelOrder(): {0}", order);

            if (!order.BrokerId.Any())
            {
                // we need the brokerage order id in order to perform a cancellation
                Log.Trace("BitfinexBrokerage.CancelOrder(): Unable to cancel order without BrokerId.");
                return false;
            }

            var parameters = new JsonObject
            {
                { "id", order.BrokerId.Select(Parse.Long).First() }
            };

            var obj = new JsonArray { 0, "oc", null, parameters };
            var json = JsonConvert.SerializeObject(obj);
            WebSocket.Send(json);

            return true;
        }

        /// <summary>
        /// Closes the websockets connection
        /// </summary>
        public override void Disconnect()
        {
            WebSocket.Close();
        }

        /// <summary>
        /// Gets all orders not yet closed
        /// </summary>
        /// <returns></returns>
        public override List<Order> GetOpenOrders()
        {
            var endpoint = GetEndpoint("auth/r/orders");
            var request = new RestRequest(endpoint, Method.POST);

            var parameters = new JsonObject();

            request.AddJsonBody(parameters.ToString());
            SignRequest(request, endpoint, parameters);

            var response = ExecuteRestRequest(request);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BitfinexBrokerage.GetOpenOrders: request failed: " +
                                    $"[{(int)response.StatusCode}] {response.StatusDescription}, " +
                                    $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var orders = JsonConvert.DeserializeObject<Messages.Order[]>(response.Content)
                .Where(OrderFilter(_algorithm.BrokerageModel.AccountType));

            var list = new List<Order>();
            foreach (var item in orders)
            {
                Order order;

                var quantity = item.Amount;
                var price = item.Price;
                var symbol = _symbolMapper.GetLeanSymbol(item.Symbol, SecurityType.Crypto, Market.Bitfinex);
                var time = Time.UnixMillisecondTimeStampToDateTime(item.MtsCreate);

                if (item.Type.Replace("EXCHANGE", "").Trim() == "MARKET")
                {
                    order = new MarketOrder(symbol, quantity, time, price);
                }
                else if (item.Type.Replace("EXCHANGE", "").Trim() == "LIMIT")
                {
                    order = new LimitOrder(symbol, quantity, price, time);
                }
                else if (item.Type.Replace("EXCHANGE", "").Trim() == "STOP")
                {
                    order = new StopMarketOrder(symbol, quantity, price, time);
                }
                else
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, (int)response.StatusCode,
                        "BitfinexBrokerage.GetOpenOrders: Unsupported order type returned from brokerage: " + item.Type));
                    continue;
                }

                order.BrokerId.Add(item.Id.ToStringInvariant());
                order.Status = ConvertOrderStatus(item);
                list.Add(order);
            }

            foreach (var item in list)
            {
                if (item.Status.IsOpen())
                {
                    var cached = CachedOrderIDs
                        .FirstOrDefault(c => c.Value.BrokerId.Contains(item.BrokerId.First()));
                    if (cached.Value != null)
                    {
                        CachedOrderIDs[cached.Key] = item;
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Gets all open positions
        /// </summary>
        /// <returns></returns>
        public override List<Holding> GetAccountHoldings()
        {
            if (_algorithm.BrokerageModel.AccountType == AccountType.Cash)
            {
                // For cash account try loading pre - existing currency swaps from the job packet if provided
                return base.GetAccountHoldings(_job?.BrokerageData, _algorithm?.Securities.Values);
            }

            var endpoint = GetEndpoint("auth/r/positions");
            var request = new RestRequest(endpoint, Method.POST);

            var parameters = new JsonObject();

            request.AddJsonBody(parameters.ToString());
            SignRequest(request, endpoint, parameters);

            var response = ExecuteRestRequest(request);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BitfinexBrokerage.GetAccountHoldings: request failed: " +
                                    $"[{(int)response.StatusCode}] {response.StatusDescription}, " +
                                    $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var positions = JsonConvert.DeserializeObject<Position[]>(response.Content);
            return positions.Where(p => p.Amount != 0 && p.Symbol.StartsWith("t"))
                .Select(ConvertHolding)
                .ToList();
        }

        /// <summary>
        /// Gets the total account cash balance for specified account type
        /// </summary>
        /// <returns></returns>
        public override List<CashAmount> GetCashBalance()
        {
            var endpoint = GetEndpoint("auth/r/wallets");
            var request = new RestRequest(endpoint, Method.POST);

            var parameters = new JsonObject();

            request.AddJsonBody(parameters.ToString());
            SignRequest(request, endpoint, parameters);

            var response = ExecuteRestRequest(request);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BitfinexBrokerage.GetCashBalance: request failed: " +
                                    $"[{(int)response.StatusCode}] {response.StatusDescription}, " +
                                    $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var availableWallets = JsonConvert.DeserializeObject<Wallet[]>(response.Content)
                .Where(WalletFilter(_algorithm.BrokerageModel.AccountType));

            var list = new List<CashAmount>();
            foreach (var item in availableWallets)
            {
                if (item.Balance > 0)
                {
                    list.Add(new CashAmount(item.Balance, GetLeanCurrency(item.Currency)));
                }
            }

            var balances = list.ToDictionary(x => x.Currency);

            if (_algorithm.BrokerageModel.AccountType == AccountType.Margin)
            {
                // include cash balances from currency swaps for open Crypto positions
                foreach (var holding in GetAccountHoldings().Where(x => x.Symbol.SecurityType == SecurityType.Crypto))
                {
                    var defaultQuoteCurrency = _algorithm.Portfolio.CashBook.AccountCurrency;
                    CurrencyPairUtil.DecomposeCurrencyPair(holding.Symbol, out var baseCurrency, out var quoteCurrency, defaultQuoteCurrency);

                    var baseQuantity = holding.Quantity;
                    CashAmount baseCurrencyAmount;
                    balances[baseCurrency] = balances.TryGetValue(baseCurrency, out baseCurrencyAmount)
                        ? new CashAmount(baseQuantity + baseCurrencyAmount.Amount, baseCurrency)
                        : new CashAmount(baseQuantity, baseCurrency);

                    var quoteQuantity = -holding.Quantity * holding.AveragePrice;
                    CashAmount quoteCurrencyAmount;
                    balances[quoteCurrency] = balances.TryGetValue(quoteCurrency, out quoteCurrencyAmount)
                        ? new CashAmount(quoteQuantity + quoteCurrencyAmount.Amount, quoteCurrency)
                        : new CashAmount(quoteQuantity, quoteCurrency);
                }
            }

            return balances.Values.ToList();
        }

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(Data.HistoryRequest request)
        {
            if (request.Symbol.SecurityType != SecurityType.Crypto)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidSecurityType",
                    $"{request.Symbol.SecurityType} security type not supported, no history returned"));
                yield break;
            }

            if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"{request.Resolution} resolution not supported, no history returned"));
                yield break;
            }

            if (request.StartTimeUtc >= request.EndTimeUtc)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidDateRange",
                    "The history request start date must precede the end date, no history returned"));
                yield break;
            }

            if (request.TickType != TickType.Trade)
            {
                if (!_loggedSupportsOnlyTradeBars)
                {
                    _loggedSupportsOnlyTradeBars = true;
                    _algorithm?.Debug("Warning: Bitfinex history provider only supports trade information, does not support quotes.");
                    Log.Error("BitfinexBrokerage.GetHistory(): Bitfinex only supports TradeBars");
                }
                yield break;
            }

            var symbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var resultionTimeSpan = request.Resolution.ToTimeSpan();
            var resolutionString = ConvertResolution(request.Resolution);
            var resolutionTotalMilliseconds = (long)request.Resolution.ToTimeSpan().TotalMilliseconds;
            var endpoint = $"{ApiVersion}/candles/trade:{resolutionString}:{symbol}/hist?limit=1000&sort=1";

            // Bitfinex API only allows to support trade bar history requests.
            // The start and end dates are expected to match exactly with the beginning of the first bar and ending of the last.
            // So we need to round up dates accordingly.
            var startTimeStamp = (long)Time.DateTimeToUnixTimeStamp(request.StartTimeUtc.RoundDown(resultionTimeSpan)) * 1000;
            var endTimeStamp = (long)Time.DateTimeToUnixTimeStamp(request.EndTimeUtc.RoundDown(resultionTimeSpan)) * 1000;

            do
            {
                var timeframe = $"&start={startTimeStamp}&end={endTimeStamp}";
                var restRequest = new RestRequest(endpoint + timeframe, Method.GET);
                var response = ExecuteRestRequest(restRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception(
                        $"BitfinexBrokerage.GetHistory: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, " +
                        $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
                }

                // Drop the last bar provided by the exchange as its open time is a history request's end time
                var candles = JsonConvert.DeserializeObject<object[][]>(response.Content)
                    .Select(entries => new Candle(entries))
                    .Where(candle => candle.Timestamp != endTimeStamp)
                    .ToList();

                // Bitfinex exchange may return us an empty result - if we request data for a small time interval
                // during which no trades occurred - so it's rational to ensure 'candles' list is not empty before
                // we proceed to avoid an exception to be thrown
                if (candles.Any())
                {
                    startTimeStamp = candles.Last().Timestamp + resolutionTotalMilliseconds;
                }
                else
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NoHistoricalData",
                        $"Exchange returned no data for {symbol} on history request " +
                        $"from {request.StartTimeUtc:s} to {request.EndTimeUtc:s}"));
                    yield break;
                }

                foreach (var candle in candles)
                {
                    yield return new TradeBar
                    {
                        Time = Time.UnixMillisecondTimeStampToDateTime(candle.Timestamp),
                        Symbol = request.Symbol,
                        Low = candle.Low,
                        High = candle.High,
                        Open = candle.Open,
                        Close = candle.Close,
                        Volume = candle.Volume,
                        Value = candle.Close,
                        DataType = MarketDataType.TradeBar,
                        Period = resultionTimeSpan,
                        EndTime = Time.UnixMillisecondTimeStampToDateTime(candle.Timestamp + (long)resultionTimeSpan.TotalMilliseconds)
                    };
                }
            } while (startTimeStamp < endTimeStamp);
        }

        #endregion IBrokerage

        #region IDataQueueHandler

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
            var apiKey = job.BrokerageData["bitfinex-api-key"];
            var apiSecret = job.BrokerageData["bitfinex-api-secret"];
            var aggregator = Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), forceTypeNameOnExisting: false);

            Initialize(
                wssUrl: WebSocketUrl,
                websocket: new WebSocketClientWrapper(),
                restClient: new RestClient(RestApiUrl),
                apiKey: apiKey,
                apiSecret: apiSecret,
                algorithm: null,
                aggregator: aggregator,
                job: job
            );

            if (!IsConnected)
            {
                Connect();
            }
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            SubscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            SubscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        #endregion IDataQueueHandler

        /// <summary>
        /// Event invocator for the Message event
        /// </summary>
        /// <param name="e">The error</param>
        public new void OnMessage(BrokerageMessageEvent e)
        {
            base.OnMessage(e);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
            _aggregator.DisposeSafely();
            _restRateLimiter.DisposeSafely();
            _connectionRateLimiter.DisposeSafely();
            _onSubscribeEvent.DisposeSafely();
            _onUnsubscribeEvent.DisposeSafely();
            SubscriptionManager.DisposeSafely();
        }

        private bool CanSubscribe(Symbol symbol)
        {
            if (symbol.Value.Contains("UNIVERSE") || !_symbolMapper.IsKnownLeanSymbol(symbol))
            {
                return false;
            }
            return symbol.ID.Market == Market.Bitfinex;
        }

        private class ModulesReadLicenseRead : Api.RestResponse
        {
            [JsonProperty(PropertyName = "license")]
            public string License;
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validate the user of this project has permission to be using it via our web API.
        /// </summary>
        private static void ValidateSubscription()
        {
            try
            {
                const int productId = 182;
                var userId = Globals.UserId;
                var token = Globals.UserToken;
                var organizationId = Globals.OrganizationID;
                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }
                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>()
                {
                    {"productId", productId},
                    {"machineName", Environment.MachineName},
                    {"userName", Environment.UserName},
                    {"domainName", Environment.UserDomainName},
                    {"os", Environment.OSVersion}
                };
                // IP and Mac Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address)).Select(x => x.ToString());
                        // If this interface has non-loopback addresses, we will include it
                        if (!addresses.IsNullOrEmpty())
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }
                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }
                // Include our OrganizationId is specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }
                var request = new RestRequest("modules/license/read", Method.POST) { RequestFormat = DataFormat.Json };
                request.AddParameter("application/json", JsonConvert.SerializeObject(information), ParameterType.RequestBody);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;
                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }
                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);
                    // Decrypt our information
                    using var aes = new AesManaged();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();
                    if (!decryptedData.IsNullOrEmpty())
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }
                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }
                if (!isValid.Value)
                {
                    throw new ArgumentException($"Your subscription is not valid, please check your product subscriptions on our website.");
                }
                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception e)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {e.Message}");
                Environment.Exit(1);
            }
        }
    }
}
