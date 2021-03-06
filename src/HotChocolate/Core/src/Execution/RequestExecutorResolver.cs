using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using HotChocolate.Configuration;
using HotChocolate.Execution.Configuration;
using HotChocolate.Execution.Errors;
using HotChocolate.Execution.Instrumentation;
using HotChocolate.Execution.Options;
using HotChocolate.Execution.Utilities;
using HotChocolate.Types.Descriptors.Definitions;
using HotChocolate.Utilities;
using static HotChocolate.Execution.Utilities.ThrowHelper;

namespace HotChocolate.Execution
{
    internal sealed class RequestExecutorResolver
        : IRequestExecutorResolver
        , IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<string, IRequestExecutor> _executors =
            new ConcurrentDictionary<string, IRequestExecutor>();
        private readonly IOptionsMonitor<RequestExecutorFactoryOptions> _optionsMonitor;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDiagnosticEvents _diagnosticEvents;
        private readonly ITypeConverter _typeConverter;
        private bool _disposed;

        public event EventHandler<RequestExecutorEvictedEventArgs>? RequestExecutorEvicted;

        public RequestExecutorResolver(
            IOptionsMonitor<RequestExecutorFactoryOptions> optionsMonitor,
            IServiceProvider serviceProvider,
            IDiagnosticEvents diagnosticEvents,
            ITypeConverter typeConverter)
        {
            _optionsMonitor = optionsMonitor ??
                throw new ArgumentNullException(nameof(optionsMonitor));
            _serviceProvider = serviceProvider ??
                throw new ArgumentNullException(nameof(serviceProvider));
            _diagnosticEvents = diagnosticEvents ??
                throw new ArgumentNullException(nameof(diagnosticEvents));
            _typeConverter = typeConverter ??
                throw new ArgumentNullException(nameof(typeConverter));
            _optionsMonitor.OnChange((options, name) => EvictRequestExecutor(name));
        }

        public async ValueTask<IRequestExecutor> GetRequestExecutorAsync(
            NameString schemaName = default,
            CancellationToken cancellationToken = default)
        {
            schemaName = schemaName.HasValue ? schemaName : Schema.DefaultName;

            if (!_executors.TryGetValue(schemaName, out IRequestExecutor? executor))
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);

                try
                {
                    if (!_executors.TryGetValue(schemaName, out executor))
                    {
                        executor = await CreateRequestExecutorAsync(schemaName, cancellationToken)
                            .ConfigureAwait(false);
                        _diagnosticEvents.ExecutorCreated(schemaName, executor);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            return executor;
        }

        public void EvictRequestExecutor(NameString schemaName = default)
        {
            schemaName = schemaName.HasValue ? schemaName : Schema.DefaultName;

            if (_executors.TryRemove(schemaName, out IRequestExecutor? executor))
            {
                _diagnosticEvents.ExecutorEvicted(schemaName, executor);
                RequestExecutorEvicted?.Invoke(
                    this,
                    new RequestExecutorEvictedEventArgs(schemaName, executor));
            }
        }

        private async Task<IRequestExecutor> CreateRequestExecutorAsync(
            NameString schemaName,
            CancellationToken cancellationToken = default)
        {
            RequestExecutorFactoryOptions options = _optionsMonitor.Get(schemaName);

            ISchema schema = await CreateSchemaAsync(schemaName, options, cancellationToken);

            RequestExecutorOptions executorOptions =
                await CreateExecutorOptionsAsync(options, cancellationToken);
            IEnumerable<IErrorFilter> errorFilters = CreateErrorFilters(options, executorOptions);
            IErrorHandler errorHandler = new DefaultErrorHandler(errorFilters, executorOptions);
            IActivator activator = new DefaultActivator(_serviceProvider);
            RequestDelegate pipeline =
                CreatePipeline(schemaName, options.Pipeline, activator, errorHandler, executorOptions);

            return new RequestExecutor(
                schema,
                _serviceProvider,
                errorHandler,
                _typeConverter,
                activator,
                _diagnosticEvents,
                pipeline);
        }

        private async ValueTask<ISchema> CreateSchemaAsync(
            NameString schemaName,
            RequestExecutorFactoryOptions options,
            CancellationToken cancellationToken)
        {
            if (options.Schema is { })
            {
                AssertSchemaNameValid(options.Schema, schemaName);
                return options.Schema;
            }

            var schemaBuilder = options.SchemaBuilder ?? new SchemaBuilder();

            foreach (SchemaBuilderAction action in options.SchemaBuilderActions)
            {
                if (action.Action is { } configure)
                {
                    configure(schemaBuilder);
                }

                if (action.AsyncAction is { } configureAsync)
                {
                    await configureAsync(schemaBuilder, cancellationToken).ConfigureAwait(false);
                }
            }

            schemaBuilder
                .AddTypeInterceptor(new SetSchemaNameInterceptor(schemaName))
                .AddServices(_serviceProvider);
        

            ISchema schema = schemaBuilder.Create();
            AssertSchemaNameValid(schema, schemaName);
            return schema;
        }

        private void AssertSchemaNameValid(ISchema schema, NameString expectedSchemaName)
        {
            if (!schema.Name.Equals(expectedSchemaName))
            {
                throw RequestExecutorResolver_SchemaNameDoesNotMatch(
                    expectedSchemaName,
                    schema.Name);
            }
        }

        private async ValueTask<RequestExecutorOptions> CreateExecutorOptionsAsync(
            RequestExecutorFactoryOptions options,
            CancellationToken cancellationToken)
        {
            var executorOptions = options.RequestExecutorOptions ?? new RequestExecutorOptions();

            foreach (RequestExecutorOptionsAction action in options.RequestExecutorOptionsActions)
            {
                if (action.Action is { } configure)
                {
                    configure(executorOptions);
                }

                if (action.AsyncAction is { } configureAsync)
                {
                    await configureAsync(executorOptions, cancellationToken).ConfigureAwait(false);
                }
            }

            return executorOptions;
        }

        private RequestDelegate CreatePipeline(
            NameString schemaName,
            IList<RequestCoreMiddleware> pipeline,
            IActivator activator,
            IErrorHandler errorHandler,
            RequestExecutorOptions executorOptions)
        {
            if (pipeline.Count == 0)
            {
                pipeline.AddDefaultPipeline();
            }

            var factoryContext = new RequestCoreMiddlewareContext(
                schemaName, _serviceProvider, activator, errorHandler, executorOptions);

            RequestDelegate next = context =>
            {
                return default;
            };

            for (int i = pipeline.Count - 1; i >= 0; i--)
            {
                next = pipeline[i](factoryContext, next);
            }

            return next;
        }

        private IEnumerable<IErrorFilter> CreateErrorFilters(
            RequestExecutorFactoryOptions options,
            RequestExecutorOptions executorOptions)
        {
            foreach (CreateErrorFilter factory in options.ErrorFilters)
            {
                yield return factory(_serviceProvider, executorOptions);
            }

            foreach (IErrorFilter errorFilter in _serviceProvider.GetServices<IErrorFilter>())
            {
                yield return errorFilter;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _executors.Clear();
                _semaphore.Dispose();
                _disposed = true;
            }
        }

        private sealed class SetSchemaNameInterceptor : TypeInterceptor
        {
            private readonly NameString _schemaName;

            public SetSchemaNameInterceptor(NameString schemaName)
            {
                _schemaName = schemaName;
            }

            public override bool CanHandle(ITypeSystemObjectContext context) =>
                context.IsSchema;

            public override void OnBeforeCompleteName(
                ITypeCompletionContext completionContext,
                DefinitionBase definition,
                IDictionary<string, object> contextData)
            {
                definition.Name = _schemaName;
            }
        }
    }
}
