using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Utilities;
using EmbedIO.Internal;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// <para>A module using objects derived from <see cref="WebApiController"/>
    /// as collections of handler methods.</para>
    /// </summary>
    public abstract partial class WebApiModuleBase : RoutingModuleBase
    {
        internal const string ReflectionMessage = "Controllers are discovered, bound and invoked through reflection and runtime code generation, "
            + "which trimming and Native AOT do not support. Register routes with RoutingModule or ActionModule delegates instead.";

        private const string GetRequestDataAsyncMethodName = nameof(IRequestDataAttribute<WebApiController>.GetRequestDataAsync);

        private static readonly MethodInfo PreProcessRequestMethod = typeof(WebApiController).GetMethod(nameof(WebApiController.PreProcessRequest)) ?? throw new MissingMemberException("PreProcessRequestMethod");
        private static readonly MethodInfo HttpContextSetter = typeof(WebApiController).GetProperty(nameof(WebApiController.HttpContext))?.GetSetMethod(true) ?? throw new MissingMemberException("HttpContextSetter");
        private static readonly MethodInfo RouteSetter = typeof(WebApiController).GetProperty(nameof(WebApiController.Route))?.GetSetMethod(true) ?? throw new MissingMemberException("RouteSetter");
        private static readonly MethodInfo AwaitResultMethod = typeof(WebApiModuleBase).GetMethod(nameof(AwaitResult), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMemberException("AwaitResultMethod");
        private static readonly MethodInfo AwaitAndCastResultMethod = typeof(WebApiModuleBase).GetMethod(nameof(AwaitAndCastResult), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMemberException("AwaitAndCastResultMethod");
        private static readonly MethodInfo DisposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose)) ?? throw new MissingMemberException("DisposeMethod");
        private static readonly MethodInfo CastControllerMethod = typeof(WebApiModuleBase).GetMethod(nameof(CastController), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMemberException("CastControllerMethod");
        private static readonly MethodInfo SerializeResultAsyncMethod = typeof(WebApiModuleBase).GetMethod(nameof(SerializeResultAsync), BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMemberException("SerializeResultAsyncMethod");

        private readonly HashSet<Type> _controllerTypes = new HashSet<Type>();
        // Method identifies the handler: a MethodInfo for reflection registration,
        // a generated handler ID string for a WebApiControllerBinding.
        private List<(HttpVerbs Verb, RouteMatcher Matcher, object Method, Type ControllerType)>? _caseInsensitiveDeclarations;
        private bool _caseInsensitiveRoutes;

        /// <summary>
        /// Gets or sets whether literal controller-route text is matched without case,
        /// using the invariant culture. The default is <see langword="false"/>.
        /// </summary>
        /// <remarks>
        /// Set before registering controllers. Module mount prefixes, captured values,
        /// request URLs, query data and other modules retain their existing behavior.
        /// Case-equivalent declarations for overlapping verbs must identify the same handler.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Configuration is locked, or the value is changed after controller registration.</exception>
        public bool CaseInsensitiveRoutes
        {
            get => _caseInsensitiveRoutes;
            set
            {
                EnsureConfigurationNotLocked();
                if (value != _caseInsensitiveRoutes && _controllerTypes.Count != 0)
                    throw new InvalidOperationException("Set CaseInsensitiveRoutes before registering controllers.");
                _caseInsensitiveRoutes = value;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiModuleBase" /> class,
        /// using the default response serializer.
        /// </summary>
        /// <param name="baseRoute">The base route served by this module.</param>
        /// <seealso cref="IWebModule.BaseRoute" />
        /// <seealso cref="Validate.RoutePath" />
        protected WebApiModuleBase(string baseRoute)
            : this(baseRoute, ResponseSerializer.Default)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiModuleBase" /> class,
        /// using the specified response serializer.
        /// </summary>
        /// <param name="baseRoute">The base route served by this module.</param>
        /// <param name="serializer">A <see cref="ResponseSerializerCallback"/> used to serialize
        /// the result of controller methods returning <see langword="object"/>
        /// or <see cref="Task{TResult}">Task&lt;object&gt;</see>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
        /// <seealso cref="IWebModule.BaseRoute" />
        /// <seealso cref="Validate.RoutePath" />
        protected WebApiModuleBase(string baseRoute, ResponseSerializerCallback serializer)
            : base(baseRoute)
        {
            Serializer = Validate.NotNull(nameof(serializer), serializer);
        }

        /// <summary>
        /// A <see cref="ResponseSerializerCallback"/> used to serialize
        /// the result of controller methods returning values.
        /// </summary>
        public ResponseSerializerCallback Serializer { get; }

        /// <summary>
        /// Gets the number of controller types registered in this module.
        /// </summary>
        public int ControllerCount => _controllerTypes.Count;

        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>In order for registration to be successful, the specified controller type:</para>
        /// <list type="bullet">
        /// <item><description>must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description>must not be an abstract class;</description></item>
        /// <item><description>must not be a generic type definition;</description></item>
        /// <item><description>must have a public parameterless constructor.</description></item>
        /// </list>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para><typeparamref name="TController"/> does not satisfy the prerequisites
        /// listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para>A new instance of <typeparamref name="TController"/> will be created
        /// for each request to handle, and dereferenced immediately afterwards,
        /// to be collected during next garbage collection cycle.</para>
        /// <para><typeparamref name="TController"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <typeparamref name="TController"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType{TController}(Func{TController})"/>
        /// <seealso cref="RegisterControllerType(Type)"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        protected void RegisterControllerType<TController>()
            where TController : WebApiController, new()
            => RegisterControllerType(typeof(TController));

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>In order for registration to be successful:</para>
        /// <list type="bullet">
        /// <item><description><typeparamref name="TController"/> must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description><typeparamref name="TController"/> must not be a generic type definition;</description></item>
        /// <item><description><paramref name="factory"/>'s return type must be either <typeparamref name="TController"/>
        /// or a subclass of <typeparamref name="TController"/>.</description></item>
        /// </list>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="factory">The factory method used to construct instances of <typeparamref name="TController"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="factory"/> does not satisfy the prerequisites listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para><paramref name="factory"/>will be called once for each request to handle
        /// in order to obtain an instance of <typeparamref name="TController"/>.
        /// The returned instance will be dereferenced immediately after handling the request.</para>
        /// <para><typeparamref name="TController"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <typeparamref name="TController"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request. In this case it is recommended that
        /// <paramref name="factory"/> return a newly-constructed instance of <typeparamref name="TController"/>
        /// at each invocation.</para>
        /// <para>If <typeparamref name="TController"/> does not implement <see cref="IDisposable"/>,
        /// <paramref name="factory"/> may employ techniques such as instance pooling to avoid
        /// the overhead of constructing a new instance of <typeparamref name="TController"/>
        /// at each invocation. If so, resources such as file handles, database connections, etc.
        /// should be freed before returning from each handler method to avoid
        /// <see href="https://en.wikipedia.org/wiki/Starvation_(computer_science)">starvation</see>.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType{TController}()"/>
        /// <seealso cref="RegisterControllerType(Type,Func{WebApiController})"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        protected void RegisterControllerType<TController>(Func<TController> factory)
            where TController : WebApiController
            => RegisterControllerType(typeof(TController), factory);

        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>In order for registration to be successful, the specified <paramref name="controllerType"/>: </para>
        /// <list type="bullet">
        /// <item><description>must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description>must not be an abstract class;</description></item>
        /// <item><description>must not be a generic type definition;</description></item>
        /// <item><description>must have a public parameterless constructor.</description></item>
        /// </list>
        /// </summary>
        /// <param name="controllerType">The type of the controller.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="controllerType"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="controllerType"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="controllerType"/> does not satisfy the prerequisites
        /// listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para>A new instance of <paramref name="controllerType"/> will be created
        /// for each request to handle, and dereferenced immediately afterwards,
        /// to be collected during next garbage collection cycle.</para>
        /// <para><paramref name="controllerType"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <paramref name="controllerType"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType(Type,Func{WebApiController})"/>
        /// <seealso cref="RegisterControllerType{TController}()"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        protected void RegisterControllerType(Type controllerType)
        {
            EnsureConfigurationNotLocked();

            controllerType = ValidateControllerType(nameof(controllerType), controllerType, false);

            var constructor = controllerType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 0);
            if (constructor == null)
            {
                throw new ArgumentException(
                    "Controller type must have a public parameterless constructor.",
                    nameof(controllerType));
            }

            if (!TryRegisterControllerTypeCore(controllerType, Expression.New(constructor)))
                throw new ArgumentException($"Type {controllerType.Name} contains no controller methods.");
        }

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>In order for registration to be successful:</para>
        /// <list type="bullet">
        /// <item><description><paramref name="controllerType"/> must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description><paramref name="controllerType"/> must not be a generic type definition;</description></item>
        /// <item><description><paramref name="factory"/>'s declared return type must be related to <paramref name="controllerType"/>:
        /// <paramref name="controllerType"/>, a subclass of it, or a base class such as <see cref="WebApiController"/>.
        /// A factory declared with a base class is checked when it creates a controller, and throws
        /// <see cref="InvalidOperationException"/> if the instance is not a <paramref name="controllerType"/>.</description></item>
        /// </list>
        /// </summary>
        /// <param name="controllerType">The type of the controller.</param>
        /// <param name="factory">The factory method used to construct instances of <paramref name="controllerType"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">
        /// <para><paramref name="controllerType"/> is <see langword="null"/>.</para>
        /// <para>- or -</para>
        /// <para><paramref name="factory"/> is <see langword="null"/>.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="controllerType"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para>One or more parameters do not satisfy the prerequisites listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para><paramref name="factory"/>will be called once for each request to handle
        /// in order to obtain an instance of <paramref name="controllerType"/>.
        /// The returned instance will be dereferenced immediately after handling the request.</para>
        /// <para><paramref name="controllerType"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <paramref name="controllerType"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request. In this case it is recommended that
        /// <paramref name="factory"/> return a newly-constructed instance of <paramref name="controllerType"/>
        /// at each invocation.</para>
        /// <para>If <paramref name="controllerType"/> does not implement <see cref="IDisposable"/>,
        /// <paramref name="factory"/> may employ techniques such as instance pooling to avoid
        /// the overhead of constructing a new instance of <paramref name="controllerType"/>
        /// at each invocation. If so, resources such as file handles, database connections, etc.
        /// should be freed before returning from each handler method to avoid
        /// <see href="https://en.wikipedia.org/wiki/Starvation_(computer_science)">starvation</see>.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType(Type)"/>
        /// <seealso cref="RegisterControllerType{TController}(Func{TController})"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        protected void RegisterControllerType(Type controllerType, Func<WebApiController> factory)
        {
            EnsureConfigurationNotLocked();

            controllerType = ValidateControllerType(nameof(controllerType), controllerType, true);
            factory = Validate.NotNull(nameof(factory), factory);

            // A lambda converted to Func<WebApiController> is compiled with that return type, so only
            // a return type unrelated to the controller is rejected here; the instance is checked when created.
            var returnType = factory.Method.ReturnType;
            if (!controllerType.IsAssignableFrom(returnType) && !returnType.IsAssignableFrom(controllerType))
                throw new ArgumentException("Factory method has an incorrect return type.", nameof(factory));

            Expression expression = Expression.Call(
                factory.Target == null ? null : Expression.Constant(factory.Target),
                factory.Method);
            if (!controllerType.IsAssignableFrom(returnType))
                expression = Expression.Call(CastControllerMethod.MakeGenericMethod(controllerType), expression);

            if (!TryRegisterControllerTypeCore(controllerType, expression))
                throw new ArgumentException($"Type {controllerType.Name} contains no controller methods.");
        }

        /// <summary>
        /// Registers request-aware activation with explicit ownership. Release is awaited
        /// after the handler and serializer, including when either throws.
        /// </summary>
        /// <param name="controllerType">The concrete controller type.</param>
        /// <param name="factory">Creates a fresh controller for the request.</param>
        /// <param name="release">Releases the controller, or does nothing for container-owned instances.</param>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        protected void RegisterControllerTypeWithContext(
            Type controllerType,
            Func<IHttpContext, WebApiController?> factory,
            Func<IHttpContext, WebApiController, Task> release)
        {
            EnsureConfigurationNotLocked();
            controllerType = ValidateControllerType(nameof(controllerType), controllerType, true);
            Validate.NotNull(nameof(factory), factory);
            Validate.NotNull(nameof(release), release);
            if (!TryRegisterControllerTypeCore(controllerType, Expression.Default(controllerType), factory, release))
                throw new ArgumentException($"Type {controllerType.Name} contains no controller methods.");
        }

        private static int IndexOfRouteParameter(RouteMatcher matcher, string name)
        {
            var names = matcher.ParameterNames;
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i] == name)
                    return i;
            }

            return -1;
        }

        private static T AwaitResult<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();

        // The constructed request data interfaces of one generic definition that a parameter's
        // attributes implement, in attribute and interface order.
        [RequiresUnreferencedCode(ReflectionMessage)]
        private static List<(Attribute Attr, Type Intf)> RequestDataInterfaces(ParameterInfo parameter, Type definition)
            => parameter.GetCustomAttributes<Attribute>()
                .SelectMany(attr => attr.GetType().GetInterfaces()
                    .Where(x => x.IsConstructedGenericType && x.GetGenericTypeDefinition() == definition)
                    .Select(x => (attr, x)))
                .ToList();

        // Checks what a factory declared with a base return type actually created.
        private static TController CastController<TController>(WebApiController? controller)
            where TController : WebApiController
            => controller as TController ?? throw new InvalidOperationException(
                $"The controller factory for {typeof(TController).FullName} returned {controller?.GetType().FullName ?? "null"}.");

        private static T? AwaitAndCastResult<T>(string parameterName, Task<object> task)
            => RequestFieldBinding.CastResult<T>(parameterName, task.ConfigureAwait(false).GetAwaiter().GetResult());

        private static bool IsGenericTaskType(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Type? resultType)
        {
            resultType = null;

            if (!type.IsConstructedGenericType)
                return false;

            if (type.GetGenericTypeDefinition() != typeof(Task<>))
                return false;

            resultType = type.GetGenericArguments()[0];
            return true;
        }

        // Compile a handler.
        //
        // Parameters:
        // - factoryExpression is an Expression that builds a controller;
        // - method is a MethodInfo for a public instance method of the controller;
        // - route is the route to which the controller method is associated.
        //
        // This method builds a lambda, with the same signature as a RouteHandlerCallback<IHttpContext>, that:
        // - uses factoryExpression to build a controller;
        // - calls the controller method, passing converted route parameters for method parameters with matching names
        //   and default values for other parameters;
        // - serializes the returned object (or the result of the returned task),
        //   unless the return type of the controller method is void or Task;
        // - if the controller implements IDisposable, disposes it.
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private RouteHandlerCallback CompileHandler(
            Expression factoryExpression, MethodInfo method, RouteMatcher matcher,
            Func<IHttpContext, WebApiController?>? contextFactory,
            Func<IHttpContext, WebApiController, Task>? release, BeforeHandlerPlan? before)
        {
            // Lambda parameters
            var contextInLambda = Expression.Parameter(typeof(IHttpContext), "context");
            var routeInLambda = Expression.Parameter(typeof(RouteMatch), "route");

            // Local variable for controller
            var controllerType = method.ReflectedType ?? throw new InvalidOperationException("The handler has no reflected controller type.");
            var controller = Expression.Variable(controllerType, "controller");

            // Build the handler method call, then wrap its result in a Task that serializes it when needed.
            var handlerArguments = BindHandlerArguments(method, controllerType, matcher, controller, routeInLambda);
            var callMethod = WrapHandlerResult(Expression.Call(controller, method, handlerArguments), method.ReturnType, contextInLambda);

            if (contextFactory == null && IsStreamingResultType(method.ReturnType))
            {
                var streamingFactory = Expression.Lambda<Func<WebApiController>>(Expression.Convert(factoryExpression, typeof(WebApiController))).Compile();
                return CompileActivatedHandler(controllerType, controller, callMethod, contextInLambda, routeInLambda,
                    _ => streamingFactory(), ReleaseStreamingController, before);
            }

            return contextFactory != null
                ? CompileActivatedHandler(controllerType, controller, callMethod, contextInLambda, routeInLambda, contextFactory, release, before)
                : before != null ? CompileHookedOwnedHandler(controllerType, controller, factoryExpression, callMethod, contextInLambda, routeInLambda, before)
                : CompileOwnedHandler(controllerType, controller, factoryExpression, callMethod, contextInLambda, routeInLambda);
        }

        // One argument expression per handler parameter: request data provided by an attribute,
        // the converted route parameter with the same name, or the parameter's default.
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static List<Expression> BindHandlerArguments(MethodInfo method, Type controllerType, RouteMatcher matcher, ParameterExpression controller, ParameterExpression routeInLambda)
        {
            var handlerArguments = new List<Expression>();
            foreach (var parameter in method.GetParameters())
            {
                var argument = BindHandlerArgument(parameter, method, controllerType, matcher, controller, routeInLambda);
                var validation = ReflectionRequestValidation.For(parameter);
                if (validation != null)
                    argument = Expression.Call(typeof(ReflectionRequestValidation), nameof(ReflectionRequestValidation.ValidateBound), new[] { parameter.ParameterType },
                        argument, Expression.Constant(parameter.Name ?? throw new InvalidOperationException("The validated parameter has no name.")), Expression.Constant(validation), Expression.Property(controller, nameof(WebApiController.CancellationToken)));
                handlerArguments.Add(argument);
            }

            return handlerArguments;
        }

        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static Expression BindHandlerArgument(ParameterInfo parameter, MethodInfo method, Type controllerType, RouteMatcher matcher, ParameterExpression controller, ParameterExpression routeInLambda)
        {
            // First, check for generic request data interfaces in attributes, then for non-generic ones.
            var requestData = TypedRequestData(parameter, controllerType, controller, out var typedAttributes);
            var untypedAttributes = false;
            if (requestData == null)
                requestData = UntypedRequestData(parameter, controllerType, controller, out untypedAttributes);
            if (requestData != null)
                return requestData;

            // There are request data attributes, but none is suitable for the type of the parameter:
            // the user expects data to be injected but provided no way of injecting the right data type.
            if (typedAttributes || untypedAttributes)
                throw new InvalidOperationException($"No request data attribute for parameter {parameter.Name} of method {controllerType.Name}.{method.Name} can provide the expected data type.");

            // Check whether the name of the handler parameter matches the name of a route parameter.
            var parameterType = parameter.ParameterType;
            var index = IndexOfRouteParameter(matcher, parameter.Name ?? throw new InvalidOperationException("The handler parameter has no name."));
            if (index >= 0)
            {
                // Convert the parameter to the handler's parameter type.
                var convertFromRoute = FromString.ConvertExpressionTo(
                    parameterType,
                    Expression.Property(routeInLambda, "Item", Expression.Constant(index)));

                return convertFromRoute ?? throw SelfCheck.Failure($"{nameof(convertFromRoute)} is null.");
            }

            // No route parameter has the same name as a handler parameter.
            // Pass the default for the parameter type.
            return parameter.HasDefaultValue
                ? (Expression)Expression.Constant(parameter.DefaultValue)
                : Expression.Default(parameterType);
        }

        // Request data from the first IRequestDataAttribute<TController, TData> attribute that applies
        // to both the controller and the parameter type; null when none applies. declared reports
        // whether the parameter carries any attribute of that kind.
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static Expression? TypedRequestData(ParameterInfo parameter, Type controllerType, ParameterExpression controller, out bool declared)
        {
            var requestDataInterfaces = RequestDataInterfaces(parameter, typeof(IRequestDataAttribute<,>));
            declared = requestDataInterfaces.Count > 0;
            if (!declared)
                return null;

            // Take the first that applies to both controller and parameter type
            var parameterType = parameter.ParameterType;
            var (attr, intf) = requestDataInterfaces.FirstOrDefault(
                x => x.Intf.GenericTypeArguments[0].IsAssignableFrom(controllerType)
                  && parameterType.IsAssignableFrom(x.Intf.GenericTypeArguments[1]));
            if (attr == null)
                return null;

            // Use the request data interface to get a value for the parameter.
            Expression useRequestDataInterface = Expression.Call(
                Expression.Constant(attr),
                intf.GetMethod(GetRequestDataAsyncMethodName) ?? throw new MissingMethodException(intf.FullName, GetRequestDataAsyncMethodName),
                controller,
                Expression.Constant(parameter.Name));

            // We should await the call to GetRequestDataAsync.
            // For lack of a better way, call AwaitResult with an appropriate type argument.
            return Expression.Call(
                AwaitResultMethod.MakeGenericMethod(intf.GenericTypeArguments[1]),
                useRequestDataInterface);
        }

        // Request data from the first IRequestDataAttribute<TController> attribute that applies to the
        // controller, cast to the parameter type; null when none applies. declared reports whether
        // the parameter carries any attribute of that kind.
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private static Expression? UntypedRequestData(ParameterInfo parameter, Type controllerType, ParameterExpression controller, out bool declared)
        {
            var requestDataInterfaces = RequestDataInterfaces(parameter, typeof(IRequestDataAttribute<>));
            declared = requestDataInterfaces.Count > 0;
            if (!declared)
                return null;

            // Take the first that applies to the controller
            var (attr, intf) = requestDataInterfaces.FirstOrDefault(
                x => x.Intf.GenericTypeArguments[0].IsAssignableFrom(controllerType));
            if (attr == null)
                return null;

            // Use the request data interface to get a value for the parameter.
            var parameterType = parameter.ParameterType;
            Expression useRequestDataInterface = Expression.Call(
                Expression.Constant(attr),
                intf.GetMethod(GetRequestDataAsyncMethodName) ?? throw new MissingMethodException(intf.FullName, GetRequestDataAsyncMethodName),
                controller,
                Expression.Constant(parameterType),
                Expression.Constant(parameter.Name));

            // We should await the call to GetRequestDataAsync,
            // then cast the result to the parameter type.
            // AwaitAndCastResult preserves synchronous binding and shares the cast
            // rules with generated handlers.
            return Expression.Call(
                AwaitAndCastResultMethod.MakeGenericMethod(parameterType),
                Expression.Constant(parameter.Name),
                useRequestDataInterface);
        }

        // Wraps the handler call in a Task: a Task passes through, void completes immediately,
        // and a Task<TResult> or a synchronous result is serialized.
        [RequiresUnreferencedCode(ReflectionMessage)]
        [RequiresDynamicCode(ReflectionMessage)]
        private Expression WrapHandlerResult(Expression callMethod, Type methodReturnType, ParameterExpression contextInLambda)
        {
            if (methodReturnType == typeof(Task))
            {
                // Nothing to do
                return callMethod;
            }

            if (methodReturnType == typeof(void))
            {
                // Convert void to Task by evaluating Task.CompletedTask
                return Expression.Block(typeof(Task), callMethod, Expression.Constant(Task.CompletedTask));
            }

            if (typeof(JsonStreamResult).IsAssignableFrom(methodReturnType))
                return Expression.Call(Expression.Convert(callMethod, typeof(JsonStreamResult)),
                    typeof(JsonStreamResult).GetMethod(nameof(JsonStreamResult.WriteAsync)) ?? throw new MissingMethodException(nameof(JsonStreamResult.WriteAsync)),
                    contextInLambda, Expression.Constant(default(System.Threading.CancellationToken)));

            if (IsGenericTaskType(methodReturnType, out var streamType) && typeof(JsonStreamResult).IsAssignableFrom(streamType))
                return Expression.Call(typeof(ControllerBindingServices), nameof(ControllerBindingServices.WriteStreamResultAsync),
                    new[] { streamType }, contextInLambda, callMethod);

            if (IsGenericTaskType(methodReturnType, out var resultType))
            {
                // Return a Task that serializes the result of a Task<TResult>
                return Expression.Call(
                    Expression.Constant(this),
                    SerializeResultAsyncMethod.MakeGenericMethod(resultType),
                    contextInLambda,
                    callMethod);
            }

            // Return a Task that serializes a result obtained synchronously
            return Expression.Call(
                Serializer.Target == null ? null : Expression.Constant(Serializer.Target),
                Serializer.Method,
                contextInLambda,
                Expression.Convert(callMethod, typeof(object)));
        }

        // Compiles binding and invocation once, with activation and the awaited release
        // outside the expression tree.
        [RequiresDynamicCode(ReflectionMessage)]
        private static RouteHandlerCallback CompileActivatedHandler(
            Type controllerType, ParameterExpression controller, Expression callMethod,
            ParameterExpression contextInLambda, ParameterExpression routeInLambda,
            Func<IHttpContext, WebApiController?> contextFactory,
            Func<IHttpContext, WebApiController, Task>? release, BeforeHandlerPlan? before)
        {
            var instance = Expression.Parameter(typeof(WebApiController), "instance");
            var invoke = Expression.Lambda<Func<WebApiController, IHttpContext, RouteMatch, Task>>(
                Expression.Block(new[] { controller },
                    Expression.Assign(controller, Expression.Convert(instance, controllerType)),
                    before == null ? Expression.Call(controller, PreProcessRequestMethod) : Expression.Empty(),
                    callMethod),
                instance, contextInLambda, routeInLambda).Compile();

            return async (context, route) =>
            {
                var created = contextFactory(context);
                if (created == null)
                    throw new InvalidOperationException($"The factory for {controllerType.FullName} returned null.");

                try
                {
                    if (!controllerType.IsInstanceOfType(created))
                        throw new InvalidOperationException($"The factory for {controllerType.FullName} returned {created.GetType().FullName}.");

                    created.HttpContext = context;
                    created.Route = route;
                    if (before != null) await before.ExecuteAsync(created, context.CancellationToken).ConfigureAwait(false);
                    await invoke(created, context, route).ConfigureAwait(false);
                }
                finally
                {
                    await (release ?? throw new InvalidOperationException("No controller release callback is configured."))(context, created).ConfigureAwait(false);
                }
            };
        }

        // The legacy path: the compiled handler creates the controller from the factory expression,
        // initializes its properties, calls the method and disposes a disposable controller.
        [RequiresDynamicCode(ReflectionMessage)]
        private static RouteHandlerCallback CompileOwnedHandler(
            Type controllerType, ParameterExpression controller, Expression factoryExpression, Expression callMethod,
            ParameterExpression contextInLambda, ParameterExpression routeInLambda)
        {
            // Label for return statement
            var returnTarget = Expression.Label(typeof(Task));

            // Operations to perform on the controller.
            // Pseudocode:
            //     controller.PreProcessRequest();
            //     return controller.method(handlerArguments);
            Expression workWithController = Expression.Block(
                Expression.Call(controller, PreProcessRequestMethod),
                Expression.Return(returnTarget, callMethod));

            // If the controller type implements IDisposable,
            // wrap operations in a simulated using block.
            if (typeof(IDisposable).IsAssignableFrom(controllerType))
            {
                // Pseudocode:
                //     try
                //     {
                //         body();
                //     }
                //     finally
                //     {
                //         (controller as IDisposable).Dispose();
                //     }
                workWithController = Expression.TryFinally(
                    workWithController,
                    Expression.Call(Expression.TypeAs(controller, typeof(IDisposable)), DisposeMethod));
            }

            // Create the controller and initialize its properties, work with it, and end the body
            // at the target of return statements. Then build and compile the lambda.
            return Expression.Lambda<RouteHandlerCallback>(
                Expression.Block(new[] { controller },
                    Expression.Assign(controller, factoryExpression),
                    Expression.Call(controller, HttpContextSetter, contextInLambda),
                    Expression.Call(controller, RouteSetter, routeInLambda),
                    workWithController,
                    Expression.Label(returnTarget, Expression.Constant(Task.FromResult(false)))),
                contextInLambda,
                routeInLambda)
                .Compile();
        }

        private async Task SerializeResultAsync<TResult>(IHttpContext context, Task<TResult> task)
        {
            await Serializer(
                context,
                await task.ConfigureAwait(false)).ConfigureAwait(false);
        }

        private Type ValidateControllerType(string argumentName, Type value, bool canBeAbstract)
        {
            value = Validate.NotNull(argumentName, value);
            if (canBeAbstract)
            {
                if (value.IsGenericTypeDefinition
                 || !value.IsSubclassOf(typeof(WebApiController)))
                    throw new ArgumentException($"Controller type must be a subclass of {nameof(WebApiController)}.", argumentName);
            }
            else
            {
                if (value.IsAbstract
                 || value.IsGenericTypeDefinition
                 || !value.IsSubclassOf(typeof(WebApiController)))
                    throw new ArgumentException($"Controller type must be a non-abstract subclass of {nameof(WebApiController)}.", argumentName);
            }

            if (_controllerTypes.Contains(value))
                throw new ArgumentException("Controller type is already registered in this module.", argumentName);

            return value;
        }

        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        private bool TryRegisterControllerTypeCore(
            Type controllerType, Expression factoryExpression,
            Func<IHttpContext, WebApiController?>? contextFactory = null,
            Func<IHttpContext, WebApiController, Task>? release = null)
        {
            var handlerCount = 0;
            var caseInsensitive = _caseInsensitiveRoutes;
            var pending = caseInsensitive ? new List<(HttpVerbs Verb, RouteMatcher Matcher, object Method, RouteHandlerCallback Handler)>() : null;
            var methods = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.ContainsGenericParameters).ToArray();
            var authorization = methods.Where(method => method.GetCustomAttributes().OfType<RouteAttribute>().Any())
                .ToDictionary(method => method, method => ReadAuthorization(controllerType, method));
            var beforeHandlers = authorization.Keys.ToDictionary(method => method, method => ReadBeforeHandler(controllerType, method));
            foreach (var metadata in authorization.Values)
                if (metadata != null) _authorization.Validate(metadata);

            foreach (var method in methods)
            {
                var attributes = method.GetCustomAttributes()
                    .OfType<RouteAttribute>()
                    .ToArray();
                if (attributes.Length < 1)
                    continue;

                foreach (var attribute in attributes)
                {
                    if (caseInsensitive)
                    {
                        var matcher = attribute.Matcher.WithCaseInsensitiveLiterals();
                        var staged = pending ?? throw new InvalidOperationException("Case-insensitive declarations have not been initialized.");
                        if (!AcceptCaseInsensitiveDeclaration(attribute.Verb, matcher, method, controllerType, staged.Select(p => (p.Verb, p.Matcher, p.Method))))
                            continue;
                        staged.Add((attribute.Verb, matcher, method, WrapAuthorization(CompileHandler(factoryExpression, method, matcher, contextFactory, release, beforeHandlers[method]), authorization[method])));
                    }
                    else
                    {
                        AddHandler(attribute.Verb, attribute.Matcher, WrapAuthorization(CompileHandler(factoryExpression, method, attribute.Matcher, contextFactory, release, beforeHandlers[method]), authorization[method]));
                    }
                    handlerCount++;
                }
            }

            if (handlerCount < 1)
                return false;

            if (pending != null)
            {
                foreach (var entry in pending)
                {
                    AddHandler(entry.Verb, entry.Matcher, entry.Handler);
                    (_caseInsensitiveDeclarations ?? throw new InvalidOperationException("Case-insensitive declarations have not been initialized.")).Add((entry.Verb, entry.Matcher, entry.Method, controllerType));
                }
            }

            _controllerTypes.Add(controllerType);
            return true;
        }

        /// <summary>
        /// <para>Registers a controller type through a binding produced by the EmbedIO-Neo source generator,
        /// constructing a new instance for each request.</para>
        /// <para>The routes, parameter binding, result serialization and disposal behave as in
        /// <see cref="RegisterControllerType{TController}()"/>, without reflection or runtime code generation.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="binding">The generated binding of <typeparamref name="TController"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="binding"/> contains no routes.</para>
        /// </exception>
        /// <seealso cref="GeneratedControllerBindingAttribute"/>
        protected void RegisterControllerType<TController>(WebApiControllerBinding<TController> binding)
            where TController : WebApiController, new()
            => RegisterBinding(binding, static () => new TController());

        /// <summary>
        /// <para>Registers a controller type through a binding produced by the EmbedIO-Neo source generator,
        /// using a factory method.</para>
        /// <para>The routes, parameter binding, result serialization and disposal behave as in
        /// <see cref="RegisterControllerType{TController}(Func{TController})"/>, without reflection or runtime code generation.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="binding">The generated binding of <typeparamref name="TController"/>.</param>
        /// <param name="factory">The factory method used to construct instances of <typeparamref name="TController"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="binding"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="binding"/> contains no routes.</para>
        /// </exception>
        /// <seealso cref="GeneratedControllerBindingAttribute"/>
        protected void RegisterControllerType<TController>(WebApiControllerBinding<TController> binding, Func<TController> factory)
            where TController : WebApiController
            => RegisterBinding(binding, Validate.NotNull(nameof(factory), factory));

        /// <summary>
        /// Registers a controller type through a binding produced by the EmbedIO-Neo source generator,
        /// with request-aware activation and explicit ownership, as in
        /// <see cref="RegisterControllerTypeWithContext(Type, Func{IHttpContext, WebApiController}, Func{IHttpContext, WebApiController, Task})"/>.
        /// Release is awaited after the handler and serializer, including when either throws.
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="binding">The generated binding of <typeparamref name="TController"/>.</param>
        /// <param name="factory">Creates a fresh controller for the request.</param>
        /// <param name="release">Releases the controller, or does nothing for container-owned instances.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="binding"/>, <paramref name="factory"/>
        /// or <paramref name="release"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="binding"/> contains no routes.</para>
        /// </exception>
        /// <seealso cref="GeneratedControllerBindingAttribute"/>
        protected void RegisterControllerTypeWithContext<TController>(
            WebApiControllerBinding<TController> binding,
            Func<IHttpContext, TController?> factory,
            Func<IHttpContext, TController, Task> release)
            where TController : WebApiController
        {
            Validate.NotNull(nameof(factory), factory);
            Validate.NotNull(nameof(release), release);
            RegisterBinding(binding, route => CreateBoundHandler(route.CreateInvoker(), Serializer, factory, release, route.UseControllerPreprocessing));
        }

        private void RegisterBinding<TController>(WebApiControllerBinding<TController> binding, Func<TController> factory)
            where TController : WebApiController
            => RegisterBinding(binding, route => IsStreamingResultType(route.Operation?.ResultType)
                ? CreateBoundHandler(route.CreateInvoker(), Serializer, _ => factory(), ReleaseStreamingController, route.UseControllerPreprocessing)
                : CreateBoundHandler(route.CreateInvoker(), Serializer, factory, route.UseControllerPreprocessing));

        private void RegisterBinding<TController>(
            WebApiControllerBinding<TController> binding,
            Func<WebApiRouteBinding<TController>, RouteHandlerCallback> createHandler)
            where TController : WebApiController
        {
            EnsureConfigurationNotLocked();
            binding = Validate.NotNull(nameof(binding), binding);
            var controllerType = ValidateControllerType(nameof(binding), typeof(TController), true);

            // Parse every route before adding any handler, so an invalid route leaves the module unchanged.
            var routes = binding.Routes;
            if (routes.Count < 1)
                throw new ArgumentException($"Type {controllerType.Name} contains no controller methods.", nameof(binding));

            var matchers = new RouteMatcher[routes.Count];
            for (var i = 0; i < routes.Count; i++)
            {
                matchers[i] = RouteMatcher.Parse(routes[i].Route, routes[i].IsBaseRoute);
                if (routes[i].Authorization is { } metadata) _authorization.Validate(metadata);
            }

            if (!_caseInsensitiveRoutes)
            {
                for (var i = 0; i < routes.Count; i++)
                    AddHandler(routes[i].Verb, matchers[i], WrapAuthorization(createHandler(routes[i]), routes[i].Authorization));
            }
            else
            {
                var pending = new List<(HttpVerbs Verb, RouteMatcher Matcher, object Method, RouteHandlerCallback Handler)>();
                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];
                    var matcher = matchers[i].WithCaseInsensitiveLiterals();
                    if (!AcceptCaseInsensitiveDeclaration(route.Verb, matcher, route.HandlerId, controllerType, pending.Select(p => (p.Verb, p.Matcher, p.Method))))
                        continue;

                    pending.Add((route.Verb, matcher, route.HandlerId, WrapAuthorization(createHandler(route), route.Authorization)));
                }

                foreach (var entry in pending)
                {
                    AddHandler(entry.Verb, entry.Matcher, entry.Handler);
                    (_caseInsensitiveDeclarations ?? throw new InvalidOperationException("Case-insensitive declarations have not been initialized.")).Add((entry.Verb, entry.Matcher, entry.Method, controllerType));
                }
            }

            _controllerTypes.Add(controllerType);
        }

        // Returns false for a case-only alias of a declaration of the same method and verb,
        // which needs no handler of its own; throws for a conflicting declaration.
        private bool AcceptCaseInsensitiveDeclaration(
            HttpVerbs verb,
            RouteMatcher matcher,
            object method,
            Type controllerType,
            IEnumerable<(HttpVerbs Verb, RouteMatcher Matcher, object Method)> pending)
        {
            _caseInsensitiveDeclarations ??= new List<(HttpVerbs, RouteMatcher, object, Type)>();
            var declarations = _caseInsensitiveDeclarations.Concat(pending.Select(p => (p.Verb, p.Matcher, p.Method, ControllerType: controllerType)));
            var overlapping = declarations.Where(d => (d.Verb == verb || d.Verb == HttpVerbs.Any || verb == HttpVerbs.Any)
                && matcher.HasCaseEquivalentTemplate(d.Matcher)).ToArray();
            if (overlapping.Any(d => !Equals(d.Method, method) || d.ControllerType != controllerType))
                throw new ArgumentException($"Case-equivalent controller route '{matcher.Route}' has conflicting handlers for overlapping HTTP verbs.");
            if (overlapping.Any(d => !d.Matcher.ParameterNames.SequenceEqual(matcher.ParameterNames)))
                throw new ArgumentException($"Case-equivalent controller route '{matcher.Route}' must retain the same parameter names.");

            // Existing case-only aliases on one method need no duplicate handler.
            return !overlapping.Any(d => d.Verb == verb && Equals(d.Method, method));
        }

        private static bool IsStreamingResultType(Type? type)
            => type != null && (typeof(JsonStreamResult).IsAssignableFrom(type)
                || IsGenericTaskType(type, out var item) && typeof(JsonStreamResult).IsAssignableFrom(item));

        private static Task ReleaseStreamingController(IHttpContext context, WebApiController controller)
        {
            if (controller is IDisposable disposable) disposable.Dispose();
            return Task.CompletedTask;
        }

        // The per-request sequence of a compiled reflection handler: create the controller,
        // initialize it, call OnBeforeHandler, bind and call the method, then dispose a
        // disposable controller as soon as the method has returned its task, before awaiting it.
        private static RouteHandlerCallback CreateBoundHandler<TController>(
            WebApiRouteInvoker<TController> invoker,
            ResponseSerializerCallback serializer,
            Func<TController> factory, bool useControllerPreprocessing)
            where TController : WebApiController
        {
            invoker = Validate.NotNull(nameof(invoker), invoker);
            var disposable = typeof(IDisposable).IsAssignableFrom(typeof(TController));
            return async (context, route) =>
            {
                var controller = factory();
                controller.HttpContext = context;
                controller.Route = route;
                Task work;
                try
                {
                    if (useControllerPreprocessing) controller.PreProcessRequest();
                    work = await invoker(controller, context, route, serializer).ConfigureAwait(false);
                }
                finally
                {
                    if (disposable)
                        ((IDisposable)controller).Dispose();
                }

                await work.ConfigureAwait(false);
            };
        }

        // The request-aware sequence of RegisterControllerTypeWithContext: release is awaited
        // after binding, invocation and serialization, including failures.
        private static RouteHandlerCallback CreateBoundHandler<TController>(
            WebApiRouteInvoker<TController> invoker,
            ResponseSerializerCallback serializer,
            Func<IHttpContext, TController?> factory,
            Func<IHttpContext, TController, Task> release, bool useControllerPreprocessing)
            where TController : WebApiController
        {
            invoker = Validate.NotNull(nameof(invoker), invoker);
            return async (context, route) =>
            {
                var controller = factory(context)
                    ?? throw new InvalidOperationException($"The factory for {typeof(TController).FullName} returned null.");

                try
                {
                    controller.HttpContext = context;
                    controller.Route = route;
                    if (useControllerPreprocessing) controller.PreProcessRequest();
                    var work = await invoker(controller, context, route, serializer).ConfigureAwait(false);
                    await work.ConfigureAwait(false);
                }
                finally
                {
                    await release(context, controller).ConfigureAwait(false);
                }
            };
        }
    }
}
