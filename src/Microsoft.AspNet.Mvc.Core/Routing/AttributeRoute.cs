// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNet.Mvc.Core;
using Microsoft.AspNet.Mvc.Internal.Routing;
using Microsoft.AspNet.Routing;
using Microsoft.AspNet.Routing.Template;
using Microsoft.Framework.Internal;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.Mvc.Routing
{
    public class AttributeRoute : IRouter
    {
        private readonly IRouter _target;
        private readonly IActionDescriptorsCollectionProvider _actionDescriptorsCollectionProvider;
        private readonly IInlineConstraintResolver _constraintResolver;

        // These loggers are used by the inner route, keep them around to avoid re-creating.
        private readonly ILogger _routeLogger;
        private readonly ILogger _constraintLogger;

        private RoutesForRequestMatch _routesForRequestMatch;
        private IDictionary<string, AttributeRouteLinkGenerationEntry> _namedEntries;
        private LinkGenerationDecisionTree _linkGenerationTree;

        public AttributeRoute(
            [NotNull] IRouter target,
            [NotNull] IActionDescriptorsCollectionProvider actionDescriptorsCollectionProvider,
            [NotNull] IInlineConstraintResolver constraintResolver,
            [NotNull] ILoggerFactory loggerFactory)
        {
            _target = target;
            _actionDescriptorsCollectionProvider = actionDescriptorsCollectionProvider;
            _constraintResolver = constraintResolver;

            _routeLogger = loggerFactory.CreateLogger<InnerAttributeRoute>();
            _constraintLogger = loggerFactory.CreateLogger(typeof(RouteConstraintMatcher).FullName);
        }

        /// <inheritdoc />
        public async Task RouteAsync(RouteContext context)
        {
            var innerRoutes = GetInnerRoutes();

            foreach (var route in innerRoutes)
            {
                var oldRouteData = context.RouteData;

                var newRouteData = new RouteData(oldRouteData);
                newRouteData.Routers.Add(route);

                try
                {
                    context.RouteData = newRouteData;
                    await route.RouteAsync(context);
                }
                finally
                {
                    if (!context.IsHandled)
                    {
                        context.RouteData = oldRouteData;
                    }
                }

                if (context.IsHandled)
                {
                    break;
                }
            }

            if (!context.IsHandled)
            {
                _routeLogger.LogVerbose("Request did not match any attribute route.");
            }
        }

        /// <inheritdoc />
        public VirtualPathData GetVirtualPath(VirtualPathContext context)
        {
            // If it's a named route we will try to generate a link directly and
            // if we can't, we will not try to generate it using an unnamed route.
            if (context.RouteName != null)
            {
                return GetVirtualPathForNamedRoute(context);
            }

            // The decision tree will give us back all entries that match the provided route data in the correct
            // order. We just need to iterate them and use the first one that can generate a link.
            var matches = _linkGenerationTree.GetMatches(context);

            foreach (var match in matches)
            {
                var path = GenerateVirtualPath(context, match.Entry);
                if (path != null)
                {
                    context.IsBound = true;
                    return path;
                }
            }

            return null;
        }

        private IEnumerable<InnerAttributeRoute> GetInnerRoutes()
        {
            var actions = _actionDescriptorsCollectionProvider.ActionDescriptors;

            // This is a safe-race. We'll never set inner back to null after initializing
            // it on startup.
            if(_routesForRequestMatch == null || _routesForRequestMatch.ActionsVersion != actions.Version)
            {
                var routes = BuildRoutes(actions);

                _routesForRequestMatch = new RoutesForRequestMatch(routes, actions.Version);
            }

            return _routesForRequestMatch.Routes
                    .OrderBy(o => o.Order)
                    .ThenBy(e => e.Precedence)
                    .ThenBy(e => e.RouteTemplate, StringComparer.Ordinal);
        }

        private IList<InnerAttributeRoute> BuildRoutes(ActionDescriptorsCollection actions)
        {
            var routeInfos = GetRouteInfos(_constraintResolver, actions.Items);

            // We're creating one AttributeRouteGenerationEntry per action. This allows us to match the intended
            // action by expected route values, and then use the TemplateBinder to generate the link.
            var linkGenerationEntries = new List<AttributeRouteLinkGenerationEntry>();
            foreach (var routeInfo in routeInfos)
            {
                linkGenerationEntries.Add(new AttributeRouteLinkGenerationEntry()
                {
                    Binder = new TemplateBinder(routeInfo.ParsedTemplate, routeInfo.Defaults),
                    Defaults = routeInfo.Defaults,
                    Constraints = routeInfo.Constraints,
                    Order = routeInfo.Order,
                    Precedence = routeInfo.Precedence,
                    RequiredLinkValues = routeInfo.ActionDescriptor.RouteValueDefaults,
                    RouteGroup = routeInfo.RouteGroup,
                    Template = routeInfo.ParsedTemplate,
                    TemplateText = routeInfo.RouteTemplate,
                    Name = routeInfo.Name,
                });
            }

            var namedEntries = new Dictionary<string, AttributeRouteLinkGenerationEntry>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in linkGenerationEntries)
            {
                // Skip unnamed entries
                if (entry.Name == null)
                {
                    continue;
                }

                // We only need to keep one AttributeRouteLinkGenerationEntry per route template
                // so in case two entries have the same name and the same template we only keep
                // the first entry.
                AttributeRouteLinkGenerationEntry namedEntry = null;
                if (namedEntries.TryGetValue(entry.Name, out namedEntry) &&
                    !namedEntry.TemplateText.Equals(entry.TemplateText, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("FormatAttributeRoute_DifferentLinkGenerationEntries_SameName");
                }
                else if (namedEntry == null)
                {
                    namedEntries.Add(entry.Name, entry);
                }
            }

            _namedEntries = namedEntries;

            // The decision tree will take care of ordering for these entries.
            _linkGenerationTree = new LinkGenerationDecisionTree(linkGenerationEntries.ToArray());

            // We're creating one AttributeRouteMatchingEntry per group, so we need to identify the distinct set of
            // groups. It's guaranteed that all members of the group have the same template and precedence,
            // so we only need to hang on to a single instance of the RouteInfo for each group.
            var distinctRouteInfosByGroup = GroupRouteInfosByGroupId(routeInfos);
            var routesForRequestMatching = new List<InnerAttributeRoute>();
            foreach (var routeInfo in distinctRouteInfosByGroup)
            {
                routesForRequestMatching.Add(new InnerAttributeRoute(
                        _target,
                        routeInfo.Name,
                        routeInfo.RouteTemplate,
                        defaults: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            { AttributeRouting.RouteGroupKey, routeInfo.RouteGroup },
                        },
                        constraints: null,
                        dataTokens: null,
                        inlineConstraintResolver: _constraintResolver
                    )
                {
                    Order = routeInfo.Order,
                    Precedence = routeInfo.Precedence
                });
            }

            return routesForRequestMatching;
        }

        private static IEnumerable<RouteInfo> GroupRouteInfosByGroupId(List<RouteInfo> routeInfos)
        {
            var routeInfosByGroupId = new Dictionary<string, RouteInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var routeInfo in routeInfos)
            {
                if (!routeInfosByGroupId.ContainsKey(routeInfo.RouteGroup))
                {
                    routeInfosByGroupId.Add(routeInfo.RouteGroup, routeInfo);
                }
            }

            return routeInfosByGroupId.Values;
        }

        private static List<RouteInfo> GetRouteInfos(
            IInlineConstraintResolver constraintResolver,
            IReadOnlyList<ActionDescriptor> actions)
        {
            var routeInfos = new List<RouteInfo>();
            var errors = new List<RouteInfo>();

            // This keeps a cache of 'Template' objects. It's a fairly common case that multiple actions
            // will use the same route template string; thus, the `Template` object can be shared.
            //
            // For a relatively simple route template, the `Template` object will hold about 500 bytes
            // of memory, so sharing is worthwhile.
            var templateCache = new Dictionary<string, RouteTemplate>(StringComparer.OrdinalIgnoreCase);

            var attributeRoutedActions = actions.Where(a => a.AttributeRouteInfo != null &&
                a.AttributeRouteInfo.Template != null);
            foreach (var action in attributeRoutedActions)
            {
                var routeInfo = GetRouteInfo(constraintResolver, templateCache, action);
                if (routeInfo.ErrorMessage == null)
                {
                    routeInfos.Add(routeInfo);
                }
                else
                {
                    errors.Add(routeInfo);
                }
            }

            if (errors.Count > 0)
            {
                var allErrors = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    errors.Select(
                        e => Resources.FormatAttributeRoute_IndividualErrorMessage(
                            e.ActionDescriptor.DisplayName,
                            Environment.NewLine,
                            e.ErrorMessage)));

                var message = Resources.FormatAttributeRoute_AggregateErrorMessage(Environment.NewLine, allErrors);
                throw new InvalidOperationException(message);
            }

            return routeInfos;
        }

        private static RouteInfo GetRouteInfo(
            IInlineConstraintResolver constraintResolver,
            Dictionary<string, RouteTemplate> templateCache,
            ActionDescriptor action)
        {
            var constraint = action.RouteConstraints
                .Where(c => c.RouteKey == AttributeRouting.RouteGroupKey)
                .FirstOrDefault();
            if (constraint == null ||
                constraint.KeyHandling != RouteKeyHandling.RequireKey ||
                constraint.RouteValue == null)
            {
                // This can happen if an ActionDescriptor has a route template, but doesn't have one of our
                // special route group constraints. This is a good indication that the user is using a 3rd party
                // routing system, or has customized their ADs in a way that we can no longer understand them.
                //
                // We just treat this case as an 'opt-out' of our attribute routing system.
                return null;
            }

            var routeInfo = new RouteInfo()
            {
                ActionDescriptor = action,
                RouteGroup = constraint.RouteValue,
                RouteTemplate = action.AttributeRouteInfo.Template,
            };

            try
            {
                RouteTemplate parsedTemplate;
                if (!templateCache.TryGetValue(action.AttributeRouteInfo.Template, out parsedTemplate))
                {
                    // Parsing with throw if the template is invalid.
                    parsedTemplate = TemplateParser.Parse(action.AttributeRouteInfo.Template);
                    templateCache.Add(action.AttributeRouteInfo.Template, parsedTemplate);
                }

                routeInfo.ParsedTemplate = parsedTemplate;
            }
            catch (Exception ex)
            {
                routeInfo.ErrorMessage = ex.Message;
                return routeInfo;
            }

            foreach (var kvp in action.RouteValueDefaults)
            {
                foreach (var parameter in routeInfo.ParsedTemplate.Parameters)
                {
                    if (string.Equals(kvp.Key, parameter.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        routeInfo.ErrorMessage = Resources.FormatAttributeRoute_CannotContainParameter(
                            routeInfo.RouteTemplate,
                            kvp.Key,
                            kvp.Value);

                        return routeInfo;
                    }
                }
            }

            routeInfo.Order = action.AttributeRouteInfo.Order;

            routeInfo.Precedence = AttributeRoutePrecedence.Compute(routeInfo.ParsedTemplate);

            routeInfo.Name = action.AttributeRouteInfo.Name;

            var constraintBuilder = new RouteConstraintBuilder(constraintResolver, routeInfo.RouteTemplate);

            foreach (var parameter in routeInfo.ParsedTemplate.Parameters)
            {
                if (parameter.InlineConstraints != null)
                {
                    if (parameter.IsOptional)
                    {
                        constraintBuilder.SetOptional(parameter.Name);
                    }

                    foreach (var inlineConstraint in parameter.InlineConstraints)
                    {
                        constraintBuilder.AddResolvedConstraint(parameter.Name, inlineConstraint.Constraint);
                    }
                }
            }

            routeInfo.Constraints = constraintBuilder.Build();

            routeInfo.Defaults = routeInfo.ParsedTemplate.Parameters
                .Where(p => p.DefaultValue != null)
                .ToDictionary(p => p.Name, p => p.DefaultValue, StringComparer.OrdinalIgnoreCase);

            return routeInfo;
        }

        private VirtualPathData GetVirtualPathForNamedRoute(VirtualPathContext context)
        {
            AttributeRouteLinkGenerationEntry entry;
            if (_namedEntries.TryGetValue(context.RouteName, out entry))
            {
                var path = GenerateVirtualPath(context, entry);
                if (path != null)
                {
                    context.IsBound = true;
                    return path;
                }
            }
            return null;
        }

        private VirtualPathData GenerateVirtualPath(VirtualPathContext context,AttributeRouteLinkGenerationEntry entry)
        {
            // In attribute the context includes the values that are used to select this entry - typically
            // these will be the standard 'action', 'controller' and maybe 'area' tokens. However, we don't
            // want to pass these to the link generation code, or else they will end up as query parameters.
            //
            // So, we need to exclude from here any values that are 'required link values', but aren't
            // parameters in the template.
            //
            // Ex:
            //      template: api/Products/{action}
            //      required values: { id = "5", action = "Buy", Controller = "CoolProducts" }
            //
            //      result: { id = "5", action = "Buy" }
            var inputValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in context.Values)
            {
                if (entry.RequiredLinkValues.ContainsKey(kvp.Key))
                {
                    var parameter = entry.Template.Parameters
                        .FirstOrDefault(p => string.Equals(p.Name, kvp.Key, StringComparison.OrdinalIgnoreCase));

                    if (parameter == null)
                    {
                        continue;
                    }
                }

                inputValues.Add(kvp.Key, kvp.Value);
            }

            var bindingResult = entry.Binder.GetValues(context.AmbientValues, inputValues);
            if (bindingResult == null)
            {
                // A required parameter in the template didn't get a value.
                return null;
            }

            var matched = RouteConstraintMatcher.Match(
                entry.Constraints,
                bindingResult.CombinedValues,
                context.Context,
                this,
                RouteDirection.UrlGeneration,
                _constraintLogger);

            if (!matched)
            {
                // A constraint rejected this link.
                return null;
            }

            // These values are used to signal to the next route what we would produce if we round-tripped
            // (generate a link and then parse). In MVC the 'next route' is typically the MvcRouteHandler.
            var providedValues = new Dictionary<string, object>(
                bindingResult.AcceptedValues,
                StringComparer.OrdinalIgnoreCase);
            providedValues.Add(AttributeRouting.RouteGroupKey, entry.RouteGroup);

            var childContext = new VirtualPathContext(context.Context, context.AmbientValues, context.Values)
            {
                ProvidedValues = providedValues,
            };

            var pathData = _target.GetVirtualPath(childContext);
            if (pathData != null)
            {
                // If path is non-null then the target router short-circuited, we don't expect this
                // in typical MVC scenarios.
                return pathData;
            }
            else if (!childContext.IsBound)
            {
                // The target router has rejected these values. We don't expect this in typical MVC scenarios.
                return null;
            }

            var path = entry.Binder.BindValues(bindingResult.AcceptedValues);
            if (path == null)
            {
                return null;
            }

            return new VirtualPathData(this, path);
        }

        private class RouteInfo
        {
            public ActionDescriptor ActionDescriptor { get; set; }

            public IReadOnlyDictionary<string, IRouteConstraint> Constraints { get; set; }

            public IReadOnlyDictionary<string, object> Defaults { get; set; }

            public string ErrorMessage { get; set; }

            public RouteTemplate ParsedTemplate { get; set; }

            public int Order { get; set; }

            public decimal Precedence { get; set; }

            public string RouteGroup { get; set; }

            public string RouteTemplate { get; set; }

            public string Name { get; set; }
        }

        private class RoutesForRequestMatch
        {
            public RoutesForRequestMatch(IList<InnerAttributeRoute> routes, int actionsVersion)
            {
                Routes = routes;
                ActionsVersion = actionsVersion;
            }

            public IList<InnerAttributeRoute> Routes { get; }

            /// <summary>
            /// Gets the version of this route. This corresponds to the value of
            /// <see cref="ActionDescriptorsCollection.Version"/> when this route was created.
            /// </summary>
            public int ActionsVersion { get; }
        }
    }
}
