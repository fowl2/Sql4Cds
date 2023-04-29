﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace MarkMpn.Sql4Cds.Engine.ExecutionPlan
{
    /// <summary>
    /// Implements an UPDATE operation
    /// </summary>
    class UpdateNode : BaseDmlNode
    {
        private int _executionCount;
        private readonly Timer _timer = new Timer();

        public override int ExecutionCount => _executionCount;

        public override TimeSpan Duration => _timer.Duration;

        /// <summary>
        /// The logical name of the entity to update
        /// </summary>
        [Category("Update")]
        [Description("The logical name of the entity to update")]
        public string LogicalName { get; set; }

        /// <summary>
        /// The column that contains the primary ID of the records to update
        /// </summary>
        [Category("Update")]
        [Description("The column that contains the primary ID of the records to update")]
        [DisplayName("PrimaryId Source")]
        public string PrimaryIdSource { get; set; }

        /// <summary>
        /// The columns to update and the associated column to take the new value from
        /// </summary>
        [Category("Update")]
        [Description("The columns to update and the associated column to take the new value from")]
        [DisplayName("Column Mappings")]
        public IDictionary<string, UpdateMapping> ColumnMappings { get; } = new Dictionary<string, UpdateMapping>(StringComparer.OrdinalIgnoreCase);

        [Category("Update")]
        public override int MaxDOP { get; set; }

        [Category("Update")]
        public override int BatchSize { get; set; }

        [Category("Update")]
        public override bool BypassCustomPluginExecution { get; set; }

        [Browsable(false)]
        public IDictionary<int, StatusWithState> StateTransitions { get; set; }

        [Category("Update")]
        [Description("The state transition graph that will be navigated automatically when applying updates")]
        [DisplayName("State Transitions")]
        public IDictionary<string, Transitions> StateTransitionsDisplay => StateTransitions == null ? null : StateTransitions.Values.ToDictionary(s => $"{s.Name} ({s.StatusCode})", s => new Transitions(s.Transitions.Keys.Select(t => $"{t.Name} ({t.StatusCode})").OrderBy(n => n)));

        public override void AddRequiredColumns(NodeCompilationContext context, IList<string> requiredColumns)
        {
            if (!requiredColumns.Contains(PrimaryIdSource))
                requiredColumns.Add(PrimaryIdSource);

            foreach (var col in ColumnMappings.Values)
            {
                if (col.OldValueColumn != null && !requiredColumns.Contains(col.OldValueColumn))
                    requiredColumns.Add(col.OldValueColumn);

                if (col.NewValueColumn != null && !requiredColumns.Contains(col.NewValueColumn))
                    requiredColumns.Add(col.NewValueColumn);
            }

            Source.AddRequiredColumns(context, requiredColumns);
        }

        public override string Execute(NodeExecutionContext context, out int recordsAffected)
        {
            _executionCount++;

            try
            {
                if (!context.DataSources.TryGetValue(DataSource, out var dataSource))
                    throw new QueryExecutionException("Missing datasource " + DataSource);

                List<Entity> entities;
                EntityMetadata meta;
                Dictionary<string, AttributeMetadata> attributes;
                Dictionary<string, Func<Entity, object>> newAttributeAccessors;
                Dictionary<string, Func<Entity, object>> oldAttributeAccessors;
                Func<Entity, object> primaryIdAccessor;

                using (_timer.Run())
                {
                    entities = GetDmlSourceEntities(context, out var schema);

                    // Precompile mappings with type conversions
                    meta = dataSource.Metadata[LogicalName];
                    attributes = meta.Attributes.ToDictionary(a => a.LogicalName, StringComparer.OrdinalIgnoreCase);
                    var dateTimeKind = context.Options.UseLocalTimeZone ? DateTimeKind.Local : DateTimeKind.Utc;
                    var fullMappings = new Dictionary<string, UpdateMapping>(ColumnMappings);
                    fullMappings[meta.PrimaryIdAttribute] = new UpdateMapping { OldValueColumn = PrimaryIdSource, NewValueColumn = PrimaryIdSource };
                    newAttributeAccessors = CompileColumnMappings(dataSource, LogicalName, fullMappings.Where(kvp => kvp.Value.NewValueColumn != null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.NewValueColumn), schema, dateTimeKind, entities);
                    oldAttributeAccessors = CompileColumnMappings(dataSource, LogicalName, fullMappings.Where(kvp => kvp.Value.OldValueColumn != null).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OldValueColumn), schema, dateTimeKind, entities);
                    primaryIdAccessor = newAttributeAccessors[meta.PrimaryIdAttribute];
                }

                // Check again that the update is allowed. Don't count any UI interaction in the execution time
                var confirmArgs = new ConfirmDmlStatementEventArgs(entities.Count, meta, BypassCustomPluginExecution);
                if (context.Options.CancellationToken.IsCancellationRequested)
                    confirmArgs.Cancel = true;
                context.Options.ConfirmUpdate(confirmArgs);
                if (confirmArgs.Cancel)
                    throw new OperationCanceledException("UPDATE cancelled by user");

                var isSysAdminOrBackOfficeIntegrationUser = new Lazy<bool>(() =>
                {
                    // Check if the current user is a system administrator by looking for the known role guid
                    // https://learn.microsoft.com/en-us/power-apps/developer/data-platform/security-roles
                    var roleQry = new Microsoft.Xrm.Sdk.Query.QueryExpression("role");
                    roleQry.Criteria.AddCondition("roletemplateid", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, new Guid("627090FF-40A3-4053-8790-584EDC5BE201"));
                    var userRoles = roleQry.AddLink("systemuserroles", "roleid", "roleid");
                    userRoles.LinkCriteria.AddCondition("systemuserid", Microsoft.Xrm.Sdk.Query.ConditionOperator.EqualUserId);
                    roleQry.ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("roleid");
                    roleQry.TopCount = 1;
                    var sysAdminRoles = dataSource.Connection.RetrieveMultiple(roleQry).Entities;

                    if (sysAdminRoles.Any())
                        return true;

                    // If the current user is not a sysadmin, check if it is an integration user and SOP integration is enabled
                    // This only applies to salesorder and invoice, not quote
                    if (LogicalName == "quote")
                        return false;

                    var orgQry = new Microsoft.Xrm.Sdk.Query.QueryExpression("organization");
                    orgQry.ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("issopintegrationenabled", "integrationuserid");
                    var org = dataSource.Connection.RetrieveMultiple(orgQry).Entities[0];
                    var isSOPIntegrationEnabled = org.GetAttributeValue<bool>("issopintegrationenabled");
                    var integrationUserId = org.GetAttributeValue<Guid>("integrationuserid");

                    if (!isSOPIntegrationEnabled)
                        return false;

                    var userQry = new Microsoft.Xrm.Sdk.Query.QueryExpression("systemuser");
                    userQry.Criteria.AddCondition("systemuserid", Microsoft.Xrm.Sdk.Query.ConditionOperator.EqualUserId);
                    userQry.ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("isintegrationuser");
                    var user = dataSource.Connection.RetrieveMultiple(userQry).Entities[0];
                    var isIntegrationUser = user.GetAttributeValue<bool>("isintegrationuser");

                    return isIntegrationUser || integrationUserId == user.Id;
                }, LazyThreadSafetyMode.ExecutionAndPublication);

                using (_timer.Run())
                {
                    return ExecuteDmlOperation(
                        dataSource.Connection,
                        context.Options,
                        entities,
                        meta,
                        entity =>
                        {
                            var preImage = ExtractEntity(entity, meta, attributes, oldAttributeAccessors, primaryIdAccessor);
                            var update = ExtractEntity(entity, meta, attributes, newAttributeAccessors, primaryIdAccessor);

                            var updateRequest = new UpdateRequest { Target = update };
                            var requests = new OrganizationRequestCollection();

                            var requestedState = update.GetAttributeValue<OptionSetValue>("statecode");
                            var requestedStatus = update.GetAttributeValue<OptionSetValue>("statuscode");
                            var currentState = preImage.GetAttributeValue<OptionSetValue>("statecode");
                            var currentStatus = preImage.GetAttributeValue<OptionSetValue>("statuscode");

                            if (requestedState == null && requestedStatus != null)
                                requestedState = GetStateCode(meta, requestedStatus.Value);
                            else if (requestedState != null && requestedStatus == null)
                                requestedStatus = GetDefaultStatusCode(meta, requestedState.Value);

                            if ((LogicalName == "quote" || LogicalName == "salesorder" || LogicalName == "invoice") &&
                                currentState?.Value != 0 &&
                                !isSysAdminOrBackOfficeIntegrationUser.Value)
                            {
                                // QOI records can only be updated if they are in an editable state or the user is a sysadmin or integration user
                                // Add a request to change the status back to editable before making the update, then change the status back again
                                // afterwards
                                var defaultDraftStatusCode = GetDefaultStatusCode(meta, 0);

                                requests.Insert(0, new UpdateRequest
                                {
                                    Target = new Entity(LogicalName, update.Id)
                                    {
                                        ["statecode"] = new OptionSetValue(0),
                                        ["statuscode"] = defaultDraftStatusCode
                                    }
                                });

                                if (requestedState == null)
                                {
                                    requestedState = currentState;
                                    update["statecode"] = requestedState;
                                }

                                if (requestedStatus == null)
                                {
                                    requestedStatus = currentStatus;
                                    update["statuscode"] = requestedStatus;
                                }

                                currentState = new OptionSetValue(0);
                                currentStatus = defaultDraftStatusCode;
                            }

                            if ((requestedState != null || requestedStatus != null) && StateTransitions != null)
                            {
                                update.Attributes.Remove("statecode");
                                update.Attributes.Remove("statuscode");
                            }

                            if (update.Attributes.Any())
                                requests.Add(updateRequest);

                            if (requestedStatus != null && StateTransitions != null)
                                AddStateTransitions(update, currentStatus, requestedStatus, requests);

                            if (requests.Count == 1)
                                return requests[0];

                            return new ExecuteTransactionRequest { Requests = requests };
                        },
                        new OperationNames
                        {
                            InProgressUppercase = "Updating",
                            InProgressLowercase = "updating",
                            CompletedLowercase = "updated"
                        },
                        out recordsAffected,
                        context.ParameterValues);
                }
            }
            catch (QueryExecutionException ex)
            {
                if (ex.Node == null)
                    ex.Node = this;

                throw;
            }
            catch (Exception ex)
            {
                throw new QueryExecutionException(ex.Message, ex) { Node = this };
            }
        }

        private void AddStateTransitions(Entity entity, OptionSetValue currentStatus, OptionSetValue requestedStatus, OrganizationRequestCollection requests)
        {
            if (!StateTransitions.TryGetValue(currentStatus.Value, out var startNode))
                throw new QueryExecutionException("Unknown current status code " + currentStatus.Value);

            if (!StateTransitions.TryGetValue(requestedStatus.Value, out var endNode))
                throw new QueryExecutionException("Unknown requested status code " + requestedStatus.Value);

            var states = BfsSearchStateTransitions(startNode, endNode);

            for (var i = 1; i < states.Count; i++)
            {
                var transition = states[i - 1].Transitions[states[i]];
                var request = transition(entity, states[i]);
                requests.Add(request);
            }
        }

        private List<StatusWithState> BfsSearchStateTransitions(StatusWithState startNode, StatusWithState endNode)
        {
            var prevNode = new Dictionary<StatusWithState, StatusWithState>
            {
                [startNode] = null
            };
            var distance = new Dictionary<StatusWithState, int>
            {
                [startNode] = 0
            };
            var queue = new Queue<StatusWithState>();
            queue.Enqueue(startNode);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();

                if (node == endNode)
                    break;

                var nextDistance = distance[node] + 1;

                foreach (var nextNode in node.Transitions.Keys)
                {
                    if (distance.ContainsKey(nextNode))
                        continue;

                    distance[nextNode] = nextDistance;
                    prevNode[nextNode] = node;
                    queue.Enqueue(nextNode);
                }
            }

            var path = new List<StatusWithState>();
            var pathNode = endNode;

            while (pathNode != startNode)
            {
                if (!prevNode.TryGetValue(pathNode, out var prev))
                    throw new QueryExecutionException("No transition available from status code " + startNode.StatusCode + " to " + endNode.StatusCode);

                path.Insert(0, pathNode);
                pathNode = prev;
            }

            path.Insert(0, startNode);

            return path;
        }

        private OptionSetValue GetStateCode(EntityMetadata meta, int statuscode)
        {
            var statusCode = meta
                .Attributes
                .OfType<StatusAttributeMetadata>()
                .Single(a => a.LogicalName == "statuscode")
                .OptionSet
                .Options
                .Single(o => o.Value == statuscode);

            return new OptionSetValue((int)((StatusOptionMetadata)statusCode).State);
        }

        private OptionSetValue GetDefaultStatusCode(EntityMetadata meta, int statecode)
        {
            var stateCode = meta
                .Attributes
                .OfType<StateAttributeMetadata>()
                .Single(a => a.LogicalName == "statecode")
                .OptionSet
                .Options
                .Single(o => o.Value == statecode);

            return new OptionSetValue((int)((StateOptionMetadata)stateCode).DefaultStatus);
        }

        private Entity ExtractEntity(Entity entity, EntityMetadata meta, Dictionary<string, AttributeMetadata> attributes, Dictionary<string, Func<Entity, object>> newAttributeAccessors, Func<Entity, object> primaryIdAccessor)
        {
            var update = new Entity(LogicalName, (Guid)primaryIdAccessor(entity));

            foreach (var attributeAccessor in newAttributeAccessors)
            {
                if (attributeAccessor.Key == meta.PrimaryIdAttribute)
                    continue;

                var attr = attributes[attributeAccessor.Key];

                if (!String.IsNullOrEmpty(attr.AttributeOf))
                    continue;

                var value = attributeAccessor.Value(entity);

                update[attr.LogicalName] = value;
            }

            return update;
        }

        protected override void RenameSourceColumns(IDictionary<string, string> columnRenamings)
        {
            if (columnRenamings.TryGetValue(PrimaryIdSource, out var primaryIdSourceRenamed))
                PrimaryIdSource = primaryIdSourceRenamed;

            foreach (var kvp in ColumnMappings.ToList())
            {
                if (kvp.Value.OldValueColumn != null && columnRenamings.TryGetValue(kvp.Value.OldValueColumn, out var oldRenamed))
                    ColumnMappings[kvp.Key].OldValueColumn = oldRenamed;

                if (kvp.Value.NewValueColumn != null && columnRenamings.TryGetValue(kvp.Value.NewValueColumn, out var newRenamed))
                    ColumnMappings[kvp.Key].NewValueColumn = newRenamed;
            }
        }

        public override string ToString()
        {
            return "UPDATE";
        }

        public override object Clone()
        {
            var clone = new UpdateNode
            {
                BypassCustomPluginExecution = BypassCustomPluginExecution,
                DataSource = DataSource,
                Index = Index,
                Length = Length,
                LogicalName = LogicalName,
                MaxDOP = MaxDOP,
                PrimaryIdSource = PrimaryIdSource,
                StateTransitions = StateTransitions,
                Source = (IExecutionPlanNodeInternal)Source.Clone(),
                Sql = Sql
            };

            foreach (var kvp in ColumnMappings)
                clone.ColumnMappings.Add(kvp);

            clone.Source.Parent = clone;
            return clone;
        }
    }

    class UpdateMapping
    {
        public string OldValueColumn { get; set; }
        public string NewValueColumn { get; set; }
    }

    class Transitions : List<string>
    {
        public Transitions(IEnumerable<string> values) : base(values)
        {
        }
    }
}
