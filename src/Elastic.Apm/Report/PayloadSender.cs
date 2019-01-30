using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Elastic.Apm.Api;
using System.Threading.Tasks.Dataflow;
using Elastic.Apm.Config;
using Elastic.Apm.Logging;
using Elastic.Apm.Model.Payload;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Elastic.Apm.Report
{
	/// <summary>
	/// Responsible for sending the data to the server.
	/// Each instance creates its own thread to do the work. Therefore instances should be reused if possible.
	/// </summary>
	internal class PayloadSender : IDisposable, IPayloadSender
	{
		private readonly ScopedLogger _logger;

		private readonly HttpClient _httpClient;

		private readonly JsonSerializerSettings _settings;

		private static readonly int DnsTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;

		static PayloadSender() => ServicePointManager.DnsRefreshTimeout = DnsTimeout;

		internal PayloadSender(IApmLogger logger, IConfigurationReader configurationReader)
		{
			_logger = logger?.Scoped(nameof(PayloadSender));
			_settings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };

			var serverUrlBase = configurationReader.ServerUrls.First();
			var servicePoint = ServicePointManager.FindServicePoint(serverUrlBase);

			servicePoint.ConnectionLeaseTimeout = DnsTimeout;
			servicePoint.ConnectionLimit = 20;

			_httpClient = new HttpClient
			{
				BaseAddress = serverUrlBase
			};

			var workerThread = new Thread(StartWork)
			{
				IsBackground = true
			};
			workerThread.Start();
		}
		private readonly BatchBlock<object> _queue =
			new BatchBlock<object>(batchSize: 1, dataflowBlockOptions: new GroupingDataflowBlockOptions() { BoundedCapacity = 1_000_000 });

		public void QueuePayload(IPayload payload) => _queue.SendAsync(payload);

		public void QueueError(IError error) => _queue.SendAsync(error);

		private async void StartWork()
		{
			while (await _queue.OutputAvailableAsync())
			{
				var batch = await _queue.ReceiveAsync();

				var item = batch.FirstOrDefault();
				try
				{
					var json = JsonConvert.SerializeObject(item, _settings);
					var content = new StringContent(json, Encoding.UTF8, "application/json");

					HttpResponseMessage result = null;
					switch (item)
					{
						case Payload _:
							result = await _httpClient.PostAsync(Consts.IntakeV1Transactions, content);
							break;
						case Error _:
							result = await _httpClient.PostAsync(Consts.IntakeV1Errors, content);
							break;
					}

					// TODO: handle unsuccesful status codes
				}
				catch (Exception e)
				{
					switch (item)
					{
						case Payload p:
							_logger.LogWarning(nameof(PayloadSender), "Failed sending transaction {TransactionName}", p.Transactions.FirstOrDefault()?.Name);
							_logger.LogDebugException(e);
							break;
						case Error err:
							_logger.LogWarning(nameof(PayloadSender), "Failed sending Error {ErrorId}", err.Errors[0]?.Id);
							_logger.LogDebugException(e);
							break;
					}
				}
			}
			// ReSharper disable once FunctionNeverReturns
		}

		public void Dispose()
		{
			_httpClient.Dispose();
			_queue.Complete();
		}
	}
}
