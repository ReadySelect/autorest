﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutoRest.Core;
using AutoRest.Core.Model;
using AutoRest.Core.Logging;
using AutoRest.Core.Utilities;
using AutoRest.Core.Utilities.Collections;
using AutoRest.Swagger.Model;
using AutoRest.Swagger.Properties;
using ParameterLocation = AutoRest.Swagger.Model.ParameterLocation;
using static AutoRest.Core.Utilities.DependencyInjection;
using AutoRest.Swagger.Validation.Core;

namespace AutoRest.Swagger
{
    public class SwaggerModeler : Modeler
    {
        private const string BaseUriParameterName = "BaseUri";

        internal Dictionary<string, string> ExtendedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, CompositeType> GeneratedTypes = new Dictionary<string, CompositeType>();
        internal Dictionary<Schema, CompositeType> GeneratingTypes = new Dictionary<Schema, CompositeType>();

        public SwaggerModeler() 
        {
            if (Settings.Instance == null)
            {
                throw new ArgumentNullException("settings");
            }
        }

        public override string Name
        {
            get { return "Swagger"; }
        }

        /// <summary>
        /// Swagger service model.
        /// </summary>
        public ServiceDefinition ServiceDefinition { get; set; }

        /// <summary>
        /// Client model.
        /// </summary>
        public CodeModel CodeModel { get; set; }

        /// <summary>
        /// Builds service model from swagger file.
        /// </summary>
        /// <returns></returns>
        public override CodeModel Build()
        {
            Logger.Instance.Log(Category.Info, Resources.ParsingSwagger);
            if (string.IsNullOrWhiteSpace(Settings.Input))
            {
                throw ErrorManager.CreateError(Resources.InputRequired);
            }
            var serviceDefinition = SwaggerParser.Load(Settings.Input, Settings.FileSystemInput);
            return Build(serviceDefinition);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
        public CodeModel Build(ServiceDefinition serviceDefinition)
        {
            ServiceDefinition = serviceDefinition;
            if (Settings.Instance.CodeGenerator.EqualsIgnoreCase("None"))
            {
                // Look for semantic errors and warnings in the document.
                var validator = new RecursiveObjectValidator(PropertyNameResolver.JsonName);
                foreach (var validationEx in validator.GetValidationExceptions(ServiceDefinition.FilePath, ServiceDefinition, new ServiceDefinitionMetadata
                {   // LEGACY MODE! set defaults for the metadata, marked to be deprecated
                    ServiceDefinitionDocumentType = ServiceDefinitionDocumentType.ARM, 
                    MergeState = ServiceDefinitionDocumentState.Composed
                }))
                {
                    Logger.Instance.Log(validationEx);
                }
                return New<CodeModel>();
            }

            Logger.Instance.Log(Category.Info, Resources.GeneratingClient);
            // Update settings
            UpdateSettings();

            InitializeClientModel();
            BuildCompositeTypes();

            // Build client parameters
            foreach (var swaggerParameter in ServiceDefinition.Parameters.Values)
            {
                var parameter = ((ParameterBuilder)swaggerParameter.GetBuilder(this)).Build();

                var clientProperty = New<Property>();
                clientProperty.LoadFrom(parameter);
                clientProperty.RealPath = new string[] { parameter.SerializedName.Value };

                CodeModel.Add(clientProperty);
            }

            var  methods = new List<Method>();
            // Build methods
            foreach (var path in ServiceDefinition.Paths.Concat(ServiceDefinition.CustomPaths))
            {
                foreach (var verb in path.Value.Keys)
                {
                    var operation = path.Value[verb];
                    if (string.IsNullOrWhiteSpace(operation.OperationId))
                    {
                        throw ErrorManager.CreateError(
                            string.Format(CultureInfo.InvariantCulture,
                                Resources.OperationIdMissing,
                                verb,
                                path.Key));
                    }
                    var methodName = GetMethodName(operation);
                    var methodGroup = GetMethodGroup(operation);

                    if (verb.ToHttpMethod() != HttpMethod.Options)
                    {
                        string url = path.Key;
                        if (url.Contains("?"))
                        {
                            url = url.Substring(0, url.IndexOf('?'));
                        }
                        var method = BuildMethod(verb.ToHttpMethod(), url, methodName, operation);
                        method.Group = methodGroup;
                        
                        methods.Add(method);
                        if (method.DefaultResponse.Body is CompositeType)
                        {
                            CodeModel.AddError((CompositeType)method.DefaultResponse.Body);
                        }
                    }
                    else
                    {
                        Logger.Instance.Log(Category.Warning, Resources.OptionsNotSupported);
                    }
                }
            }

            // Set base type
            foreach (var typeName in GeneratedTypes.Keys)
            {
                var objectType = GeneratedTypes[typeName];
                if (ExtendedTypes.ContainsKey(typeName))
                {
                    objectType.BaseModelType = GeneratedTypes[ExtendedTypes[typeName]];
                }

                CodeModel.Add(objectType);
            }
            CodeModel.AddRange(methods);
            

            return CodeModel;
        }

        /// <summary>
        /// Copares two versions of the same service specification.
        /// </summary>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
        public override IEnumerable<LogMessage> Compare()
        {
            var settings = Settings.Instance;

            Logger.Instance.Log(Category.Info, Resources.ParsingSwagger);
            if (string.IsNullOrWhiteSpace(Settings.Input) || string.IsNullOrWhiteSpace(Settings.Previous))
            {
                throw ErrorManager.CreateError(Resources.InputRequired);
            }

            var oldDefintion = SwaggerParser.Load(settings.Previous, settings.FileSystemInput);
            var newDefintion = SwaggerParser.Load(settings.Input, settings.FileSystemInput);

            var context = new ComparisonContext(oldDefintion, newDefintion);

            // Look for semantic errors and warnings in the new document.
            var validator = new RecursiveObjectValidator(PropertyNameResolver.JsonName);
            var LogMessages = validator.GetValidationExceptions(newDefintion.FilePath, newDefintion, new ServiceDefinitionMetadata
            {
                ServiceDefinitionDocumentType = ServiceDefinitionDocumentType.ARM,
                MergeState = ServiceDefinitionDocumentState.Composed
            }).ToList();

            // Only compare versions if the new version is correct.
            var comparisonMessages = 
                !LogMessages.Any(m => m.Severity > Category.Error) ? 
                newDefintion.Compare(context, oldDefintion) : 
                Enumerable.Empty<ComparisonMessage>();

            return LogMessages
                .Select(msg => new ComparisonMessage(new MessageTemplate { Id = 0, Message = msg.Message }, msg.Path, msg.Severity))
                .Concat(comparisonMessages);
        }

        private void UpdateSettings()
        {
            if (ServiceDefinition.Info.CodeGenerationSettings != null)
            {
                foreach (var key in ServiceDefinition.Info.CodeGenerationSettings.Extensions.Keys)
                {
                    //Don't overwrite settings that come in from the command line
                    if (!this.Settings.CustomSettings.ContainsKey(key))
                        this.Settings.CustomSettings[key] = ServiceDefinition.Info.CodeGenerationSettings.Extensions[key];
                }
                Settings.PopulateSettings(this.Settings, this.Settings.CustomSettings);
            }
        }

        /// <summary>
        /// Initialize the base service and populate global service properties
        /// </summary>
        /// <returns>The base ServiceModel Service</returns>
        public virtual void InitializeClientModel()
        {
            if (string.IsNullOrEmpty(ServiceDefinition.Swagger))
            {
                throw ErrorManager.CreateError(Resources.UnknownSwaggerVersion);
            }

            if (ServiceDefinition.Info == null)
            {
                throw ErrorManager.CreateError(Resources.InfoSectionMissing);
            }

            CodeModel = New<CodeModel>();

            if (string.IsNullOrWhiteSpace(Settings.ClientName) && ServiceDefinition.Info.Title == null)
            {
                throw ErrorManager.CreateError(Resources.TitleMissing);
            }

            CodeModel.Name = ServiceDefinition.Info.Title?.Replace(" ", "");

            CodeModel.Namespace = Settings.Namespace;
            CodeModel.ModelsName = Settings.ModelsName;
            CodeModel.ApiVersion = ServiceDefinition.Info.Version;
            CodeModel.Documentation = ServiceDefinition.Info.Description;
            CodeModel.BaseUrl = string.Format(CultureInfo.InvariantCulture, "{0}://{1}{2}",
                ServiceDefinition.Schemes[0].ToString().ToLower(),
                ServiceDefinition.Host, ServiceDefinition.BasePath);

            // Copy extensions
            ServiceDefinition.Extensions.ForEach(extention => CodeModel.Extensions.AddOrSet(extention.Key, extention.Value));
        }

        /// <summary>
        /// Build composite types from definitions
        /// </summary>
        public virtual void BuildCompositeTypes()
        {
            // Build service types and validate allOf
            if (ServiceDefinition.Definitions != null)
            {
                foreach (var schemaName in ServiceDefinition.Definitions.Keys.ToArray())
                {
                    var schema = ServiceDefinition.Definitions[schemaName];
                    schema.GetBuilder(this).BuildServiceType(schemaName);

                    Resolver.ExpandAllOf(schema);
                    var parent = string.IsNullOrEmpty(schema.Extends.StripDefinitionPath())
                        ? null
                        : ServiceDefinition.Definitions[schema.Extends.StripDefinitionPath()];

                    if (parent != null &&
                        !AncestorsHaveProperties(parent.Properties, parent.Extends))
                    {
                        throw ErrorManager.CreateError(Resources.InvalidAncestors, schemaName);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively traverse the schema's extends to verify that it or one of it's parents
        /// has at least one property
        /// </summary>
        /// <param name="properties">The schema's properties</param>
        /// <param name="extends">The schema's extends</param>
        /// <returns>True if one or more properties found in this schema or in it's ancestors. False otherwise</returns>
        private bool AncestorsHaveProperties(Dictionary<string, Schema> properties, string extends)
        {
            if (properties.IsNullOrEmpty() && string.IsNullOrEmpty(extends))
            {
                return false;
            }

            if (!properties.IsNullOrEmpty())
            {
                return true;
            }

            extends = extends.StripDefinitionPath();
            Debug.Assert(!string.IsNullOrEmpty(extends) && ServiceDefinition.Definitions.ContainsKey(extends));
            return AncestorsHaveProperties(ServiceDefinition.Definitions[extends].Properties,
                ServiceDefinition.Definitions[extends].Extends);
        }

        /// <summary>
        /// Builds method from swagger operation.
        /// </summary>
        /// <param name="httpMethod"></param>
        /// <param name="url"></param>
        /// <param name="name"></param>
        /// <param name="operation"></param>
        /// <returns></returns>
        public Method BuildMethod(HttpMethod httpMethod, string url, string name,
            Operation operation)
        {
            string methodGroup = GetMethodGroup(operation);
            var operationBuilder = new OperationBuilder(operation, this);
            Method method = operationBuilder.BuildMethod(httpMethod, url, name, methodGroup);
            return method;
        }

        /// <summary>
        /// Extracts method group from operation ID.
        /// </summary>
        /// <param name="operation">The swagger operation.</param>
        /// <returns>Method group name or null.</returns>
        public static string GetMethodGroup(Operation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }

            if (operation.OperationId == null || operation.OperationId.IndexOf('_') == -1)
            {
                return null;
            }

            var parts = operation.OperationId.Split('_');
            return parts[0];
        }

        /// <summary>
        /// Extracts method name from operation ID.
        /// </summary>
        /// <param name="operation">The swagger operation.</param>
        /// <returns>Method name.</returns>
        public static string GetMethodName(Operation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException("operation");
            }

            if (operation.OperationId == null)
            {
                return null;
            }

            if (operation.OperationId.IndexOf('_') == -1)
            {
                return operation.OperationId;
            }

            var parts = operation.OperationId.Split('_');
            return parts[1];
        }

        public SwaggerParameter Unwrap(SwaggerParameter swaggerParameter)
        {
            if (swaggerParameter == null)
            {
                throw new ArgumentNullException("swaggerParameter");
            }

            // If referencing global parameters serializationProperty
            if (swaggerParameter.Reference != null)
            {
                string referenceKey = swaggerParameter.Reference.StripParameterPath();
                if (!ServiceDefinition.Parameters.ContainsKey(referenceKey))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.InvariantCulture,
                        Resources.DefinitionDoesNotExist, referenceKey));
                }

                swaggerParameter = ServiceDefinition.Parameters[referenceKey];
            }

            // Unwrap the schema if in "body"
            if (swaggerParameter.Schema != null && swaggerParameter.In == ParameterLocation.Body)
            {
                swaggerParameter.Schema = Resolver.Unwrap(swaggerParameter.Schema);
            }

            return swaggerParameter;
        }

        public SchemaResolver Resolver
        {
            get { return new SchemaResolver(this); }
        }
    }
}
