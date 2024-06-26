﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using MarkMpn.Sql4Cds.Engine;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using XrmToolBox.Extensibility;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Tooling.Connector;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;

namespace MarkMpn.Sql4Cds.XTB
{
    partial class ObjectExplorer : WeifenLuo.WinFormsUI.Docking.DockContent
    {
        private readonly IDictionary<string, DataSource> _dataSources;
        private readonly Action<ConnectionDetail> _newQuery;
        private readonly Action _connect;

        class LoaderParam
        {
            public Func<TreeNode, TreeNode[]> Loader;
            public TreeNode Parent;
        }

        public ObjectExplorer(IDictionary<string, DataSource> dataSources, Action<WorkAsyncInfo> workAsync, Action<ConnectionDetail> newQuery, Action connect)
        {
            InitializeComponent();

            _dataSources = dataSources;
            WorkAsync = workAsync;
            _newQuery = newQuery;
            _connect = connect;
        }

        public Action<WorkAsyncInfo> WorkAsync { get; }

        private ConnectionDetail GetService(TreeNode node)
        {
            while (node.Parent != null)
                node = node.Parent;

            var con = (ConnectionDetail)node.Tag;

            return con;
        }

        public ConnectionDetail SelectedConnection
        {
            get
            {
                if (treeView.SelectedNode == null)
                    return null;

                return GetService(treeView.SelectedNode);
            }
        }

        private void SetIcon(TreeNode node, string imageKey)
        {
            node.ImageKey = imageKey;
            node.StateImageKey = imageKey;
            node.SelectedImageKey = imageKey;
        }

        public IEnumerable<Image> GetImages()
        {
            return imageList.Images.OfType<Image>();
        }

        enum EntityType
        {
            Regular,
            Archive,
            RecycleBin
        }

        private TreeNode[] LoadEntities(TreeNode parent, EntityType entityType)
        {
            var connection = GetService(parent);
            var metadata = EntityCache.GetEntities(connection.MetadataCacheLoader, connection.ServiceClient);
            var recycleBinEntities = _dataSources[connection.ConnectionName].Metadata.RecycleBinEntities;

            return metadata
                .Where(e =>
                    entityType == EntityType.Regular ||
                    (entityType == EntityType.Archive && (e.IsArchivalEnabled == true || e.IsRetentionEnabled == true)) ||
                    (entityType == EntityType.RecycleBin && recycleBinEntities.Contains(e.LogicalName))
                )
                .OrderBy(e => e.LogicalName)
                .Select(e =>
                {
                    var node = new TreeNode(e.LogicalName);
                    node.Tag = e;
                    SetIcon(node, "Entity");
                    var attrsNode = node.Nodes.Add("Attributes");
                    SetIcon(attrsNode, "Folder");
                    AddVirtualChildNodes(attrsNode, LoadAttributes);

                    if (entityType != EntityType.Archive)
                    {
                        var relsNode = node.Nodes.Add("Relationships");
                        SetIcon(relsNode, "Folder");
                        AddVirtualChildNodes(relsNode, LoadRelationships);
                    }

                    switch (entityType)
                    {
                        case EntityType.Regular:
                            parent.Tag = "dbo";
                            break;

                        case EntityType.Archive:
                            parent.Tag = "archive";
                            break;

                        case EntityType.RecycleBin:
                            parent.Tag = "bin";
                            break;
                    }

                    return node;
                })
                .ToArray();
        }

        private TreeNode[] LoadMetadata(TreeNode parent)
        {
            var metadata = MetaMetadataCache.GetMetadata();

            return metadata
                .OrderBy(e => e.LogicalName)
                .Select(e =>
                {
                    var node = new TreeNode(e.LogicalName);
                    node.Tag = e;
                    SetIcon(node, "Entity");
                    var attrsNode = node.Nodes.Add("Attributes");
                    SetIcon(attrsNode, "Folder");
                    AddVirtualChildNodes(attrsNode, LoadAttributes);
                    var relsNode = node.Nodes.Add("Relationships");
                    SetIcon(relsNode, "Folder");
                    AddVirtualChildNodes(relsNode, LoadRelationships);
                    return node;
                })
                .ToArray();
        }

        public void AddConnection(ConnectionDetail con)
        {
            var svc = con.ServiceClient;

            EntityCache.TryGetEntities(con.MetadataCacheLoader, svc, out _);

            var conNode = treeView.Nodes.Add(con.ConnectionName);
            conNode.Tag = con;
            conNode.ContextMenuStrip = serverContextMenuStrip;
            SetIcon(conNode, "Environment");

            AddConnectionChildNodes(con, svc, conNode);
        }

        private void AddConnectionChildNodes(ConnectionDetail con, CrmServiceClient svc, TreeNode conNode)
        {
            var entitiesNode = conNode.Nodes.Add("Entities");
            SetIcon(entitiesNode, "Folder");
            AddVirtualChildNodes(entitiesNode, parent => LoadEntities(parent, EntityType.Regular));

            if (new Uri(con.OrganizationServiceUrl).Host.EndsWith(".dynamics.com"))
            {
                var archivalNode = conNode.Nodes.Add("Long Term Retention");
                SetIcon(archivalNode, "Folder");
                AddVirtualChildNodes(archivalNode, parent => LoadEntities(parent, EntityType.Archive));
            }

            if (_dataSources[con.ConnectionName].Metadata.RecycleBinEntities != null)
            {
                var recycleBinNode = conNode.Nodes.Add("Recycle Bin");
                SetIcon(recycleBinNode, "Folder");
                AddVirtualChildNodes(recycleBinNode, parent => LoadEntities(parent, EntityType.RecycleBin));
            }

            var metadataNode = conNode.Nodes.Add("Metadata");
            SetIcon(metadataNode, "Folder");
            AddVirtualChildNodes(metadataNode, LoadMetadata);
            var programmabilityNode = conNode.Nodes.Add("Programmability");
            SetIcon(programmabilityNode, "Folder");
            var tvfNode = programmabilityNode.Nodes.Add("Table-valued Functions");
            SetIcon(tvfNode, "Folder");
            var sprocNode = programmabilityNode.Nodes.Add("Stored Procedures");
            SetIcon(sprocNode, "Folder");
            treeView.SelectedNode = conNode;

            foreach (var msg in _dataSources[con.ConnectionName].MessageCache.GetAllMessages().OrderBy(tvf => tvf.Name))
            {
                if (msg.IsValidAsTableValuedFunction())
                {
                    var n = tvfNode.Nodes.Add(msg.Name);
                    n.Tag = msg;
                    n.ImageIndex = 25;
                    n.SelectedImageIndex = 25;
                }

                if (msg.IsValidAsStoredProcedure())
                {
                    var n = sprocNode.Nodes.Add(msg.Name);
                    n.Tag = msg;
                    n.ImageIndex = 26;
                    n.SelectedImageIndex = 26;
                }
            }

            if (new Uri(con.OrganizationServiceUrl).Host.EndsWith(".dynamics.com") &&
                new Version(con.OrganizationVersion) >= new Version("9.1.0.17437"))
            {
                var tsqlNode = conNode.Nodes.Add("TDS Endpoint");

                if (TDSEndpoint.IsEnabled(svc))
                {
                    if (!String.IsNullOrEmpty(svc.CurrentAccessToken))
                    {
                        tsqlNode.ImageIndex = 21;
                        tsqlNode.SelectedImageIndex = 21;
                    }
                    else
                    {
                        tsqlNode.Text += " (Unavailable - OAuth authentication required)";
                        tsqlNode.ImageIndex = 22;
                        tsqlNode.SelectedImageIndex = 22;
                    }
                }
                else
                {
                    tsqlNode.Text += " (Disabled)";
                    tsqlNode.ImageIndex = 20;
                    tsqlNode.SelectedImageIndex = 20;
                }

                tsqlNode.ContextMenuStrip = tsqlContextMenuStrip;
            }

            conNode.Expand();
        }

        private TreeNode[] LoadAttributes(TreeNode parent)
        {
            var logicalName = parent.Parent.Text;

            var metadata = (EntityMetadata)parent.Parent.Tag;
            
            if (metadata.Attributes == null)
                metadata = _dataSources[GetService(parent).ConnectionName].Metadata[logicalName];

            return metadata.Attributes
                .Where(a => a.AttributeOf == null)
                .SelectMany(a =>
                {
                    var node = new TreeNode(a.LogicalName);
                    node.Tag = a;
                    SetIcon(node, GetIconType(a));

                    var nodes = new List<TreeNode>();
                    nodes.Add(node);

                    if (a is EnumAttributeMetadata || a is BooleanAttributeMetadata || a is LookupAttributeMetadata)
                    {
                        var nameNode = new TreeNode(a.LogicalName + "name");
                        nameNode.Tag = a;
                        SetIcon(nameNode, "Text");
                        nodes.Add(nameNode);
                    }

                    if (a is LookupAttributeMetadata lookup && lookup.Targets.Length > 1 && lookup.AttributeType != AttributeTypeCode.PartyList)
                    {
                        var typeNode = new TreeNode(a.LogicalName + "type");
                        typeNode.Tag = a;
                        SetIcon(typeNode, "Text");
                        nodes.Add(typeNode);
                    }

                    return nodes;
                })
                .OrderBy(n => n.Text)
                .ToArray();
        }

        private TreeNode[] LoadRelationships(TreeNode parent)
        {
            var logicalName = parent.Parent.Text;

            var metadata = (EntityMetadata)parent.Parent.Tag;
            
            if (metadata.OneToManyRelationships == null)
            {
                metadata = ((RetrieveMetadataChangesResponse)GetService(parent).ServiceClient.Execute(new RetrieveMetadataChangesRequest
                {
                    Query = new EntityQueryExpression
                    {
                        Criteria = new MetadataFilterExpression
                        {
                            Conditions =
                            {
                                new MetadataConditionExpression(nameof(EntityMetadata.LogicalName), MetadataConditionOperator.Equals, metadata.LogicalName)
                            }
                        },
                        Properties = new MetadataPropertiesExpression
                        {
                            PropertyNames =
                            {
                                nameof(EntityMetadata.OneToManyRelationships),
                                nameof(EntityMetadata.ManyToOneRelationships),
                                nameof(EntityMetadata.ManyToManyRelationships)
                            }
                        },
                        RelationshipQuery = new RelationshipQueryExpression
                        {
                            Properties = new MetadataPropertiesExpression
                            {
                                PropertyNames =
                                {
                                    nameof(OneToManyRelationshipMetadata.SchemaName),
                                    nameof(OneToManyRelationshipMetadata.ReferencingEntity),
                                    nameof(OneToManyRelationshipMetadata.ReferencingAttribute),
                                    nameof(OneToManyRelationshipMetadata.ReferencedEntity),
                                    nameof(OneToManyRelationshipMetadata.ReferencedAttribute),
                                    nameof(ManyToManyRelationshipMetadata.Entity1LogicalName),
                                    nameof(ManyToManyRelationshipMetadata.Entity2LogicalName),
                                    nameof(ManyToManyRelationshipMetadata.IntersectEntityName),
                                    nameof(ManyToManyRelationshipMetadata.Entity1IntersectAttribute),
                                    nameof(ManyToManyRelationshipMetadata.Entity2IntersectAttribute)
                                }
                            }
                        }
                    }
                })).EntityMetadata[0];
            }

            return metadata.OneToManyRelationships.Select(r =>
                {
                    var node = new TreeNode(r.SchemaName);
                    node.Tag = r;
                    SetIcon(node, "OneToMany");
                    return node;
                })
                .Union(metadata.ManyToOneRelationships.Select(r =>
                {
                    var node = new TreeNode(r.SchemaName);
                    node.Tag = r;
                    SetIcon(node, "ManyToOne");
                    return node;
                }))
                .Union(metadata.ManyToManyRelationships.Select(r =>
                {
                    var node = new TreeNode(r.SchemaName);
                    node.Tag = r;
                    SetIcon(node, "ManyToMany");
                    return node;
                }))
                .OrderBy(node => node.Text)
                .ToArray();
        }

        private string GetIconType(AttributeMetadata a)
        {
            switch (a.AttributeType.Value)
            {
                case AttributeTypeCode.BigInt:
                case AttributeTypeCode.Integer:
                    return "Integer";

                case AttributeTypeCode.Boolean:
                case AttributeTypeCode.Picklist:
                case AttributeTypeCode.State:
                case AttributeTypeCode.Status:
                    return "OptionSet";

                case AttributeTypeCode.Customer:
                case AttributeTypeCode.Owner:
                case AttributeTypeCode.PartyList:
                    return "Owner";

                case AttributeTypeCode.DateTime:
                    return "DateTime";

                case AttributeTypeCode.Decimal:
                    return "Decimal";

                case AttributeTypeCode.Double:
                    return "Double";

                case AttributeTypeCode.Lookup:
                    return "Lookup";

                case AttributeTypeCode.Memo:
                    return "Multiline";

                case AttributeTypeCode.Money:
                    return "Currency";

                case AttributeTypeCode.String:
                case AttributeTypeCode.Virtual:
                    return "Text";

                case AttributeTypeCode.Uniqueidentifier:
                    return "UniqueIdentifier";

                default:
                    return null;
            }
        }

        private void AddVirtualChildNodes(TreeNode node, Func<TreeNode, TreeNode[]> loader)
        {
            var child = node.Nodes.Add("Loading...");
            child.ForeColor = SystemColors.HotTrack;
            SetIcon(child, "Loading");
            node.Collapse();
            child.Tag = loader;
        }

        private void LoadChildNodes(LoaderParam loader)
        {
            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading...",
                Work = (worker, args) =>
                {
                    args.Result = loader.Loader(loader.Parent);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show(args.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    var result = args.Result as TreeNode[];
                    if (result != null)
                    {
                        loader.Parent.TreeView.BeginUpdate();

                        foreach (var child in result)
                            loader.Parent.Nodes.Add(child);

                        loader.Parent.Nodes.RemoveAt(0);

                        loader.Parent.TreeView.EndUpdate();
                    }
                }
            });
        }

        private void treeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Action.HasFlag(TreeViewAction.Expand) && e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Tag is Func<TreeNode, TreeNode[]> loader)
            {
                LoadChildNodes(new LoaderParam { Loader = loader, Parent = e.Node });
            }
        }

        private void treeView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (!(e.Node.Tag is EntityMetadata || e.Node.Tag is AttributeMetadata || e.Node.Tag is RelationshipMetadataBase))
                return;

            if (DockPanel.ActiveDocument == null)
                return;

            var query = (SqlQueryControl)DockPanel.ActiveDocument;

            if (e.Node.Tag is OneToManyRelationshipMetadata oneToMany)
            {
                var join = $@"
{oneToMany.ReferencingEntity}
INNER JOIN {oneToMany.ReferencedEntity}
    ON {oneToMany.ReferencingEntity.Split('.').Last()}.{oneToMany.ReferencingAttribute} = {oneToMany.ReferencedEntity.Split('.').Last()}.{oneToMany.ReferencedAttribute}";

                query.InsertText(join);
            }
            else if (e.Node.Tag is ManyToManyRelationshipMetadata manyToMany)
            {
                var con = GetService(e.Node);
                var entities = ((RetrieveMetadataChangesResponse)con.ServiceClient.Execute(new RetrieveMetadataChangesRequest
                {
                    Query = new EntityQueryExpression
                    {
                        Criteria = new MetadataFilterExpression
                        {
                            Conditions =
                            {
                                new MetadataConditionExpression(nameof(EntityMetadata.LogicalName), MetadataConditionOperator.In, new[] { manyToMany.Entity1LogicalName, manyToMany.Entity2LogicalName })
                            }
                        },
                        Properties = new MetadataPropertiesExpression
                        {
                            PropertyNames =
                            {
                                nameof(EntityMetadata.LogicalName),
                                nameof(EntityMetadata.PrimaryIdAttribute)
                            }
                        }
                    }
                })).EntityMetadata;
                var entity1 = entities.Single(entity => entity.LogicalName == manyToMany.Entity1LogicalName);
                var entity2 = entities.Single(entity => entity.LogicalName == manyToMany.Entity2LogicalName);

                var join = $@"
{manyToMany.Entity1LogicalName}
INNER JOIN {manyToMany.IntersectEntityName}
    ON {manyToMany.Entity1LogicalName}.{entity1.PrimaryIdAttribute} = {manyToMany.IntersectEntityName}.{manyToMany.Entity1IntersectAttribute}
INNER JOIN {manyToMany.Entity2LogicalName}
    ON {manyToMany.Entity2LogicalName}.{entity2.PrimaryIdAttribute} = {manyToMany.IntersectEntityName}.{manyToMany.Entity2IntersectAttribute}";

                query.InsertText(join);
            }
            else if (e.Node.Tag is EntityMetadata)
            {
                // Use schema name when adding table to ensure we use live or retained data as appropriate
                query.InsertText((string)e.Node.Parent.Tag + "." + e.Node.Text);
            }
            else
            {
                query.InsertText(e.Node.Text);
            }
        }

        private void contextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            enableTSQLToolStripMenuItem.Enabled = treeView.SelectedNode.ImageIndex == 20;
            disableTSQLToolStripMenuItem.Enabled = treeView.SelectedNode.ImageIndex != 20;
        }

        private void enableTSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var con = GetService(treeView.SelectedNode);
            var node = treeView.SelectedNode;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Enabling...",
                Work = (worker, args) =>
                {
                    TDSEndpoint.Enable(con.ServiceClient);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show("Error enabling TDS Endpoint:\r\n\r\n" + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    node.Text = "TDS Endpoint";

                    if (!String.IsNullOrEmpty(con.ServiceClient.CurrentAccessToken))
                    {
                        node.ImageIndex = 21;
                        node.SelectedImageIndex = 21;
                    }
                    else
                    {
                        node.ImageIndex = 21;
                        node.SelectedImageIndex = 21;
                        node.Text += " (Unavailable - OAuth authentication required)";
                    }
                }
            });
        }

        private void disableTSQLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var con = GetService(treeView.SelectedNode);
            var node = treeView.SelectedNode;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Disabling...",
                Work = (worker, args) =>
                {
                    TDSEndpoint.Disable(con.ServiceClient);
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show("Error disabling TDS Endpoint:\r\n\r\n" + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    node.Text = "TDS Endpoint (Disabled)";
                    node.ImageIndex = 20;
                    node.SelectedImageIndex = 20;
                }
            });
        }

        private void treeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            treeView.SelectedNode = e.Node;
        }

        private void newQueryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var con = GetService(treeView.SelectedNode);
            _newQuery(con);
        }

        private void disconnectToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var node = treeView.SelectedNode;

            while (node.Parent != null)
                node = node.Parent;

            node.Remove();

            tsbDisconnect.Enabled = treeView.SelectedNode != null;
        }

        private void serverContextMenuStrip_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var con = GetService(treeView.SelectedNode);

            switch (con.MetadataCacheLoader.Status)
            {
                case TaskStatus.RanToCompletion:
                    refreshToolStripMenuItem.Enabled = con.MetadataCacheLoader.Result != null;
                    break;

                case TaskStatus.Faulted:
                    refreshToolStripMenuItem.Enabled = true;
                    break;

                default:
                    refreshToolStripMenuItem.Enabled = false;
                    break;
            }
        }

        private void refreshToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var con = GetService(treeView.SelectedNode);
            var node = treeView.SelectedNode;
            while (node.Parent != null)
                node = node.Parent;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Refreshing metadata...",
                Work = (worker, args) =>
                {
                    con.UpdateMetadataCache(con.MetadataCacheLoader.IsFaulted).ConfigureAwait(false).GetAwaiter().GetResult();
                },
                PostWorkCallBack = (args) =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show("Error refreshing metadata cache:\r\n\r\n" + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    node.Nodes.Clear();
                    AddConnectionChildNodes(con, con.ServiceClient, node);
                }
            });
        }

        private void tsbConnect_Click(object sender, EventArgs e)
        {
            _connect();
        }

        private void treeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            tsbDisconnect.Enabled = treeView.SelectedNode != null;
        }
    }
}
