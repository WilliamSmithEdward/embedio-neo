namespace EmbedIO.JsonServer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using EmbedIO.Serialization;

    /// <summary>
    /// JsonServer Module.
    /// </summary>
    public class JsonServerModule : WebModuleBase, IDisposable
    {
        private readonly SemaphoreSlim _dataLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="JsonServerModule"/> class.
        /// </summary>
        /// <param name="basePath">The base path.</param>
        /// <param name="jsonPath">The json path.</param>
        public JsonServerModule(string basePath = "/api/", string? jsonPath = null)
        : base(basePath)
        {
            JsonPath = jsonPath;
            BasePath = basePath;

            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath)) return;

            var jsonData = File.ReadAllText(jsonPath);
            Data = Json.Deserialize(jsonData);
        }

        /// <inheritdoc />
        public override bool IsFinalHandler { get; } = true;

        /// <summary>
        /// Dynamic database.
        /// </summary>
        public dynamic Data { get; } = null!;

        /// <summary>
        /// Default JSON file path.
        /// </summary>
        public string? JsonPath { get; }

        /// <summary>
        /// Gets or sets the base path.
        /// </summary>
        /// <value>
        /// The base path.
        /// </value>
        public string BasePath { get; }

        /// <summary>
        /// Releases synchronization resources after all requests have completed.
        /// </summary>
        public void Dispose() => _dataLock.Dispose();

        /// <summary>
        /// Updates JSON file in disk.
        /// </summary>
        public void UpdateDataStore()
        {
            _dataLock.Wait();
            try
            {
                PersistDataStore();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        private void PersistDataStore()
        {
            if (string.IsNullOrWhiteSpace(JsonPath))
                return;

            File.WriteAllText(JsonPath, Json.Serialize((object)Data, true));
        }

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            await _dataLock.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                await ProcessRequestAsync(context).ConfigureAwait(false);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        private Task ProcessRequestAsync(IHttpContext context)
        {
            if (context.RequestedPath == "/")
                return context.SendDataAsync((object)Data);

            var parts = context.RequestedPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            var database = (IDictionary<string, object>)(object)Data;
            if (database == null || !database.TryGetValue(parts[0], out var table))
                throw HttpException.NotFound();

            if (table == null)
                throw HttpException.NotFound();

            var verb = context.Request.HttpVerb;

            switch (parts.Length)
            {
                case 1 when verb == HttpVerbs.Get:
                    return context.SendDataAsync((object)table);
                case 1 when verb == HttpVerbs.Post:
                    return AddRow(context, table);
                case 2:
                    {
                        foreach (IDictionary<string, object> row in (IEnumerable<object>)table)
                        {
                            if (row["id"].ToString() != parts[1]) continue;

                            switch (verb)
                            {
                                case HttpVerbs.Get:
                                    return context.SendDataAsync((object)row);
                                case HttpVerbs.Put:
                                    return UpdateRow(context, row);
                                case HttpVerbs.Delete:
                                    RemoveRow(table, row);
                                    return Task.CompletedTask;
                            }
                        }

                        break;
                    }
            }

            throw HttpException.BadRequest();
        }

        private async Task AddRow(IHttpContext context, object table)
        {
            var array = (IList<object>)table;
            array.Add(await context.GetRequestDataAsync<object>().ConfigureAwait(false));
            PersistDataStore();
        }

        private void RemoveRow(object table, IDictionary<string, object> row)
        {
            var array = (ICollection<object>)table;
            array.Remove(row);
            PersistDataStore();
        }

        private async Task UpdateRow(IHttpContext context, IDictionary<string, object> row)
        {
            var update = await context.GetRequestDataAsync<Dictionary<string, object>>().ConfigureAwait(false);

            foreach (var property in update)
            {
                row[property.Key] = property.Value;
            }

            PersistDataStore();
        }
    }
}
