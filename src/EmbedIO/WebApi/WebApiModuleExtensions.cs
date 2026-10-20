using System;
using System.Diagnostics.CodeAnalysis;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// Provides extension methods for <see cref="WebApiModule"/>.
    /// </summary>
    public static class WebApiModuleExtensions
    {
        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType{TController}()"/>
        /// for further information.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="WithController{TController}(WebApiModule,Func{TController})"/>
        /// <seealso cref="WithController(WebApiModule,Type)"/>
        /// <seealso cref="WebApiModuleBase.RegisterControllerType{TController}()"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        public static WebApiModule WithController<TController>(this WebApiModule @this)
            where TController : WebApiController, new()
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController<TController>();
            return @this;
        }

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType{TController}(Func{TController})"/>
        /// for further information.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <param name="factory">The factory method used to construct instances of <typeparamref name="TController"/>.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="WithController{TController}(WebApiModule)"/>
        /// <seealso cref="WithController(WebApiModule,Type,Func{WebApiController})"/>
        /// <seealso cref="WebApiModuleBase.RegisterControllerType{TController}(Func{TController})"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        public static WebApiModule WithController<TController>(this WebApiModule @this, Func<TController> factory)
            where TController : WebApiController
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController(factory);
            return @this;
        }

        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType(Type)"/>
        /// for further information.</para>
        /// </summary>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <param name="controllerType">The type of the controller.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="WithController(WebApiModule,Type,Func{WebApiController})"/>
        /// <seealso cref="WithController{TController}(WebApiModule)"/>
        /// <seealso cref="WebApiModuleBase.RegisterControllerType(Type)"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        public static WebApiModule WithController(this WebApiModule @this, Type controllerType)
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController(controllerType);
            return @this;
        }

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType(Type,Func{WebApiController})"/>
        /// for further information.</para>
        /// </summary>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <param name="controllerType">The type of the controller.</param>
        /// <param name="factory">The factory method used to construct instances of <paramref name="controllerType"/>.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="WithController(WebApiModule,Type)"/>
        /// <seealso cref="WithController{TController}(WebApiModule,Func{TController})"/>
        /// <seealso cref="WebApiModuleBase.RegisterControllerType(Type,Func{WebApiController})"/>
        [RequiresUnreferencedCode(WebApiModuleBase.ReflectionMessage)]
        [RequiresDynamicCode(WebApiModuleBase.ReflectionMessage)]
        public static WebApiModule WithController(
            this WebApiModule @this,
            Type controllerType,
            Func<WebApiController> factory
        )
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController(controllerType, factory);
            return @this;
        }

        /// <summary>
        /// <para>Registers a controller type through a binding produced by the EmbedIO-Neo source generator,
        /// constructing a new instance for each request.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType{TController}(WebApiControllerBinding{TController})"/>
        /// for further information. This method uses no reflection, so it works in trimmed and Native AOT applications.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <param name="binding">The generated binding of <typeparamref name="TController"/>.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="GeneratedControllerBindingAttribute"/>
        public static WebApiModule WithController<TController>(
            this WebApiModule @this,
            WebApiControllerBinding<TController> binding
        ) where TController : WebApiController, new()
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController(binding);
            return @this;
        }

        /// <summary>
        /// <para>Registers a controller type through a binding produced by the EmbedIO-Neo source generator,
        /// using a factory method.</para>
        /// <para>See <see cref="WebApiModuleBase.RegisterControllerType{TController}(WebApiControllerBinding{TController}, Func{TController})"/>
        /// for further information. This method uses no reflection, so it works in trimmed and Native AOT applications.</para>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="this">The <see cref="WebApiModule"/> on which this method is called.</param>
        /// <param name="binding">The generated binding of <typeparamref name="TController"/>.</param>
        /// <param name="factory">The factory method used to construct instances of <typeparamref name="TController"/>.</param>
        /// <returns><paramref name="this"/> with the controller type registered.</returns>
        /// <seealso cref="GeneratedControllerBindingAttribute"/>
        public static WebApiModule WithController<TController>(
            this WebApiModule @this,
            WebApiControllerBinding<TController> binding,
            Func<TController> factory
        ) where TController : WebApiController
        {
            if (@this is null) throw new System.NullReferenceException();

            @this.RegisterController(binding, factory);
            return @this;
        }
    }
}
