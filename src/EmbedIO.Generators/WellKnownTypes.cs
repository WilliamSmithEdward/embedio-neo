using Microsoft.CodeAnalysis;

namespace EmbedIO.Generators
{
    internal sealed class WellKnownTypes
    {
        private WellKnownTypes(Compilation compilation, INamedTypeSymbol generatedControllerBinding, INamedTypeSymbol webApiController,
            INamedTypeSymbol routeAttribute, INamedTypeSymbol baseRouteAttribute, INamedTypeSymbol requestDataOfController,
            INamedTypeSymbol requestDataOfControllerAndData, INamedTypeSymbol queryField, INamedTypeSymbol formField,
            INamedTypeSymbol jsonData, INamedTypeSymbol task, INamedTypeSymbol taskOfT, INamedTypeSymbol attributeUsageAttribute)
        {
            GeneratedControllerBinding = generatedControllerBinding;
            WebApiController = webApiController;
            RouteAttribute = routeAttribute;
            BaseRouteAttribute = baseRouteAttribute;
            RequestDataOfController = requestDataOfController;
            RequestDataOfControllerAndData = requestDataOfControllerAndData;
            QueryField = queryField;
            FormField = formField;
            JsonData = jsonData;
            Task = task;
            TaskOfT = taskOfT;
            AttributeUsageAttribute = attributeUsageAttribute;
            ValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
            ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            JsonSerializerContext = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
            TypeConverterAttribute = compilation.GetTypeByMetadataName("System.ComponentModel.TypeConverterAttribute");
        }

        internal INamedTypeSymbol GeneratedControllerBinding { get; }

        internal INamedTypeSymbol WebApiController { get; }

        internal INamedTypeSymbol RouteAttribute { get; }

        internal INamedTypeSymbol BaseRouteAttribute { get; }

        internal INamedTypeSymbol RequestDataOfController { get; }

        internal INamedTypeSymbol RequestDataOfControllerAndData { get; }

        internal INamedTypeSymbol QueryField { get; }

        internal INamedTypeSymbol FormField { get; }

        internal INamedTypeSymbol JsonData { get; }

        internal INamedTypeSymbol Task { get; }

        internal INamedTypeSymbol TaskOfT { get; }

        internal INamedTypeSymbol AttributeUsageAttribute { get; }

        internal INamedTypeSymbol? ValueTask { get; }

        internal INamedTypeSymbol? ValueTaskOfT { get; }

        internal INamedTypeSymbol? JsonSerializerContext { get; }

        internal INamedTypeSymbol? TypeConverterAttribute { get; }

        // Null when the compilation does not reference an EmbedIO version with generated bindings.
        internal static WellKnownTypes? Create(Compilation compilation)
        {
            var generatedControllerBinding = compilation.GetTypeByMetadataName(ControllerBindingGenerator.AttributeMetadataName);
            var webApiController = compilation.GetTypeByMetadataName("EmbedIO.WebApi.WebApiController");
            var routeAttribute = compilation.GetTypeByMetadataName("EmbedIO.Routing.RouteAttribute");
            var baseRouteAttribute = compilation.GetTypeByMetadataName("EmbedIO.Routing.BaseRouteAttribute");
            var requestDataOfController = compilation.GetTypeByMetadataName("EmbedIO.WebApi.IRequestDataAttribute`1");
            var requestDataOfControllerAndData = compilation.GetTypeByMetadataName("EmbedIO.WebApi.IRequestDataAttribute`2");
            var queryField = compilation.GetTypeByMetadataName("EmbedIO.WebApi.QueryFieldAttribute");
            var formField = compilation.GetTypeByMetadataName("EmbedIO.WebApi.FormFieldAttribute");
            var jsonData = compilation.GetTypeByMetadataName("EmbedIO.WebApi.JsonDataAttribute");
            var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var attributeUsageAttribute = compilation.GetTypeByMetadataName("System.AttributeUsageAttribute");

            if (generatedControllerBinding is null || webApiController is null || routeAttribute is null || baseRouteAttribute is null
                || requestDataOfController is null || requestDataOfControllerAndData is null || queryField is null || formField is null
                || jsonData is null || task is null || taskOfT is null || attributeUsageAttribute is null)
                return null;

            return new WellKnownTypes(compilation, generatedControllerBinding, webApiController, routeAttribute, baseRouteAttribute,
                requestDataOfController, requestDataOfControllerAndData, queryField, formField, jsonData, task, taskOfT, attributeUsageAttribute);
        }
    }
}
