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


using NodaTime;
using QuantConnect.Brokerages.Bitfinex;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Brokerages;

namespace QuantConnect.ToolBox.BitfinexDownloader
{
    /// <summary>
    /// Bitfinex Downloader class
    /// </summary>
    public class BitfinexDataDownloader : IDataDownloader, IDisposable
    {
        private readonly BitfinexBrokerage _brokerage;
        private readonly SymbolPropertiesDatabaseSymbolMapper _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(Market.Bitfinex);

        /// <summary>
        /// Initializes a new instance of the <see cref="BitfinexDataDownloader"/> class
        /// </summary>
        public BitfinexDataDownloader()
        {
            _brokerage = new BitfinexBrokerage(null, null, null, null, null);
            _brokerage.Connect();
        }

        /// <summary>
        /// Get historical data enumerable for a single symbol, type and resolution given this start and end time (in UTC).
        /// </summary>
        /// <param name="dataDownloaderGetParameters">model class for passing in parameters for historical data</param>
        /// <returns>Enumerable of base data for this symbol</returns>
        public IEnumerable<BaseData> Get(DataDownloaderGetParameters dataDownloaderGetParameters)
        {
            var symbol = dataDownloaderGetParameters.Symbol;
            var resolution = dataDownloaderGetParameters.Resolution;
            var startUtc = dataDownloaderGetParameters.StartUtc;
            var endUtc = dataDownloaderGetParameters.EndUtc;

            var historyRequest = new HistoryRequest(
                startUtc,
                endUtc,
                typeof(TradeBar),
                symbol,
                resolution,
                SecurityExchangeHours.AlwaysOpen(TimeZones.EasternStandard),
                DateTimeZone.Utc,
                resolution,
                false,
                false,
                DataNormalizationMode.Adjusted,
                TickType.Trade);

            var data = _brokerage.GetHistory(historyRequest);

            return data;
        }

        /// <summary>
        /// Creates Lean Symbol
        /// </summary>
        /// <param name="ticker"></param>
        /// <returns></returns>
        public Symbol GetSymbol(string ticker)
        {
            if (_symbolMapper.IsKnownBrokerageSymbol(ticker))
            {
                return _symbolMapper.GetLeanSymbol(ticker, SecurityType.Crypto, Market.Bitfinex);
            }
            else if (SymbolPropertiesDatabase.FromDataFolder().ContainsKey(Market.Bitfinex, ticker, SecurityType.Crypto))
            {
                return Symbol.Create(ticker, SecurityType.Crypto, Market.Bitfinex);
            }
            else
            {
                throw new Exception($"Unknown ticker symbol: {ticker}");
            }
        }

        #region Console Helper

        /// <summary>
        /// Draw a progress bar
        /// </summary>
        /// <param name="complete"></param>
        /// <param name="maxVal"></param>
        /// <param name="barSize"></param>
        /// <param name="progressCharacter"></param>
        private static void ProgressBar(long complete, long maxVal, long barSize, char progressCharacter)
        {

            decimal p = (decimal)complete / (decimal)maxVal;
            int chars = (int)Math.Floor(p / ((decimal)1 / (decimal)barSize));
            string bar = string.Empty;
            bar = bar.PadLeft(chars, progressCharacter);
            bar = bar.PadRight(Convert.ToInt32(barSize) - 1);

            Console.Write($"\r[{bar}] {(p * 100).ToStringInvariant("N2")}%");
        }

        public void Dispose()
        {
            _brokerage.Disconnect();
        }

        #endregion
    }
}
