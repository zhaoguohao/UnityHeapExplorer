﻿//#define HEAPEXPLORER_DISPLAY_REFS
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor.IMGUI.Controls;
using UnityEditor;

namespace HeapExplorer
{
    public class NativeObjectsControl : AbstractTreeView
    {
        public System.Action<GotoCommand> gotoCB;
        public System.Action<PackedNativeUnityEngineObject?> onSelectionChange;

        PackedMemorySnapshot m_snapshot;
        int m_uniqueId = 1;

        enum Column
        {
            Type,
            Name,
            Size,
            Count,
            DontDestroyOnLoad,
            IsPersistent,
            Address,
            InstanceID,

#if HEAPEXPLORER_DISPLAY_REFS
            ReferencesCount,
            ReferencedByCount
#endif
        }

        public NativeObjectsControl(string editorPrefsKey, TreeViewState state)
            : base(editorPrefsKey, state, new MultiColumnHeader(
                new MultiColumnHeaderState(new[]
                {
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Type"), width = 250, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Name"), width = 250, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Size"), width = 80, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Count"), width = 50, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("DDoL", "Don't Destroy on Load\nHas this object has been marked as DontDestroyOnLoad?"), width = 50, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Persistent", "Is this object persistent?\nAssets are persistent, objects stored in scenes are persistent, dynamically created objects are not."), width = 50, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Address"), width = 120, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("InstanceID", "InstanceID"), width = 120, autoResize = true },
#if HEAPEXPLORER_DISPLAY_REFS
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("Refs", "Refereces Count"), width = 50, autoResize = true },
                new MultiColumnHeaderState.Column() { headerContent = new GUIContent("RefBy", "ReferencedBy Count"), width = 50, autoResize = true },
#endif
                })))
        {
            multiColumnHeader.canSort = true;

            Reload();
        }

        public void Select(PackedNativeUnityEngineObject obj)
        {
            var item = FindItemByAddressRecursive(rootItem, (ulong)obj.nativeObjectAddress);
            SelectItem(item);
        }

        TreeViewItem FindItemByAddressRecursive(TreeViewItem parent, System.UInt64 address)
        {
            if (parent != null)
            {
                var item = parent as AbstractItem;
                if (item != null && item.address == address)
                    return item;

                if (parent.hasChildren)
                {
                    for (int n = 0, nend = parent.children.Count; n < nend; ++n)
                    {
                        var child = parent.children[n];

                        var value = FindItemByAddressRecursive(child, address);
                        if (value != null)
                            return value;
                    }
                }
            }

            return null;
        }

        protected override void OnSelectionChanged(TreeViewItem selectedItem)
        {
            base.OnSelectionChanged(selectedItem);

            if (onSelectionChange == null)
                return;

            var item = selectedItem as NativeObjectItem;
            if (item == null)
            {
                onSelectionChange.Invoke(null);
                return;
            }

            onSelectionChange.Invoke(item.packed);
        }

        public TreeViewItem BuildTree(PackedMemorySnapshot snapshot)
        {
            m_snapshot = snapshot;
            m_uniqueId = 1;

            var root = new TreeViewItem { id = 0, depth = -1, displayName = "Root" };
            if (m_snapshot == null)
            {
                root.AddChild(new TreeViewItem { id = 1, depth = -1, displayName = "" });
                return root;
            }

            // int=typeIndex
            var groupLookup = new Dictionary<long, GroupItem>();

            for (int n = 0, nend = m_snapshot.nativeObjects.Length; n < nend; ++n)
            {
                var no = m_snapshot.nativeObjects[n];

                GroupItem group;
                if (!groupLookup.TryGetValue(no.nativeTypesArrayIndex, out group))
                {
                    group = new GroupItem
                    {
                        id = m_uniqueId++,
                        depth = root.depth + 1,
                        displayName = ""
                    };
                    group.Initialize(m_snapshot, no.nativeTypesArrayIndex);

                    groupLookup[no.nativeTypesArrayIndex] = group;
                    root.AddChild(group);
                }

                // Derived MonoBehaviour types appear just as MonoBehaviour on the native side.
                // This is not very informative. However, the actual name can be derived from the MonoScript of such MonoBehaviour instead.
                // The following tries to find the corresponding MonoScript and uses the MonoScript name instead.
                #region Add MonoBehaviour using name of MonoScript
                if (no.nativeTypesArrayIndex == m_snapshot.coreTypes.nativeMonoBehaviour ||
                    no.nativeTypesArrayIndex == m_snapshot.coreTypes.nativeScriptableObject)
                {
                    string monoScriptName;
                    var monoScriptIndex = m_snapshot.FindNativeMonoScriptType(no.nativeObjectsArrayIndex, out monoScriptName);
                    if (monoScriptIndex != -1 && monoScriptIndex < m_snapshot.nativeTypes.Length)
                    {
                        long key = (monoScriptName.GetHashCode() << 32) | monoScriptIndex;

                        GroupItem group2;
                        if (!groupLookup.TryGetValue(key, out group2))
                        {
                            group2 = new GroupItem
                            {
                                id = m_uniqueId++,
                                depth = group.depth + 1,
                                displayName = monoScriptName,
                            };
                            group2.Initialize(m_snapshot, no.nativeTypesArrayIndex);

                            groupLookup[key] = group2;
                            group.AddChild(group2);
                        }
                        group = group2;
                    }
                }
                #endregion

                var item = new NativeObjectItem
                {
                    id = m_uniqueId++,
                    depth = group.depth + 1,
                    displayName = ""
                };
                item.Initialize(this, no);

                group.AddChild(item);
            }

            // remove groups if it contains one item only
            if (root.hasChildren)
            {
                for (int n = root.children.Count - 1; n >= 0; --n)
                {
                    var group = root.children[n];
                    if (group.children.Count == 1)
                    {
                        group.children[0].depth -= 1;
                        root.AddChild(group.children[0]);
                        root.children.RemoveAt(n);
                    }
                }
            }

            SortItemsRecursive(root, OnSortItem);
            
            return root;
        }

        protected override int OnSortItem(TreeViewItem aa, TreeViewItem bb)
        {
            var sortingColumn = multiColumnHeader.sortedColumnIndex;
            var ascending = multiColumnHeader.IsSortedAscending(sortingColumn);

            var itemA = (ascending ? aa : bb) as AbstractItem;
            var itemB = (ascending ? bb : aa) as AbstractItem;

            switch ((Column)sortingColumn)
            {
                case Column.Type:
                    return string.Compare(itemB.typeName, itemA.typeName, true);

                case Column.Size:
                    return itemA.size.CompareTo(itemB.size);

                case Column.Count:
                    return itemA.count.CompareTo(itemB.count);

                case Column.Address:
                    return itemA.address.CompareTo(itemB.address);

                case Column.DontDestroyOnLoad:
                    return itemA.isDontDestroyOnLoad.CompareTo(itemB.isDontDestroyOnLoad);

                case Column.IsPersistent:
                    return itemA.isPersistent.CompareTo(itemB.isPersistent);

                case Column.InstanceID:
                    return itemA.instanceId.CompareTo(itemB.instanceId);

#if HEAPEXPLORER_DISPLAY_REFS
                case Column.ReferencesCount:
                    return itemA.referencesCount.CompareTo(itemB.referencesCount);

                case Column.ReferencedByCount:
                    return itemA.referencedByCount.CompareTo(itemB.referencedByCount);
#endif
            }

            return 0;
        }

        ///////////////////////////////////////////////////////////////////////////
        // AbstractItem
        ///////////////////////////////////////////////////////////////////////////

        abstract class AbstractItem : AbstractTreeViewItem
        {
            public abstract string typeName { get; }
            public abstract string name { get; }
            public abstract int size { get; }
            public abstract int count { get; }
            public abstract System.UInt64 address { get; }
            public abstract bool isDontDestroyOnLoad { get; }
            public abstract bool isPersistent { get; }
            public abstract int instanceId { get; }
#if HEAPEXPLORER_DISPLAY_REFS
            public abstract int referencesCount { get; }
            public abstract int referencedByCount { get; }
#endif
        }

        ///////////////////////////////////////////////////////////////////////////
        // NativeObjectItem
        ///////////////////////////////////////////////////////////////////////////

        class NativeObjectItem : AbstractItem
        {
            NativeObjectsControl m_owner;
            RichNativeObject m_object;
            int m_referencesCount;
            int m_referencedByCount;

            public PackedNativeUnityEngineObject packed
            {
                get
                {
                    return m_object.packed;
                }
            }

#if HEAPEXPLORER_DISPLAY_REFS
            public override int referencesCount
            {
                get
                {
                    return m_referencesCount;
                }
            }

            public override int referencedByCount
            {
                get
                {
                    return m_referencedByCount;
                }
            }
#endif

            public override string typeName
            {
                get
                {
                    return m_object.type.name;
                }
            }

            public override string name
            {
                get
                {
                    return m_object.name;
                }
            }

            public override int size
            {
                get
                {
                    return m_object.size;
                }
            }

            public override int count
            {
                get
                {
                    return 0;
                }
            }

            public override System.UInt64 address
            {
                get
                {
                    return (ulong)m_object.packed.nativeObjectAddress;
                }
            }

            public override bool isDontDestroyOnLoad
            {
                get
                {
                    return m_object.isDontDestroyOnLoad;
                }
            }

            public override bool isPersistent
            {
                get
                {
                    return m_object.isPersistent;
                }
            }

            public override int instanceId
            {
                get
                {
                    return m_object.instanceId;
                }
            }

            public void Initialize(NativeObjectsControl owner, PackedNativeUnityEngineObject nativeObject)
            {
                m_owner = owner;
                m_object = new RichNativeObject(owner.m_snapshot, nativeObject.nativeObjectsArrayIndex);
#if HEAPEXPLORER_DISPLAY_REFS
                m_object.GetConnectionsCount(out m_referencesCount, out m_referencedByCount);
#endif
            }

            public override void GetItemSearchString(string[] target, out int count)
            {
                count = 0;
                target[count++] = m_object.name;
                target[count++] = m_object.type.name;
                target[count++] = string.Format(StringFormat.Address, address);
                target[count++] = instanceId.ToString();
            }
            
            public override void OnGUI(Rect position, int column)
            {
                if (column == 0)
                {
                    var ir = HeEditorGUI.SpaceL(ref position, position.height);
                    GUI.Box(ir, HeEditorStyles.cppImage, HeEditorStyles.iconStyle);

                    if (m_object.isDontDestroyOnLoad || m_object.isManager || ((m_object.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0))
                    {
                        var r = ir;
                        r.x += 5;
                        r.y += 4;
                        GUI.Box(r, new GUIContent(HeEditorStyles.warnImage, "The object does not unload automatically during scene changes, because of 'isDontDestroyOnLoad' or 'isManager' or 'hideFlags'."), HeEditorStyles.iconStyle);
                    }

                    //if (m_object.gcHandle.isValid)
                    //{
                    //    if (HeEditorGUI.GCHandleButton(HeEditorGUI.SpaceR(ref position, position.height)))
                    //    {
                    //        m_owner.gotoCB(new GotoCommand(m_object.gcHandle));
                    //    }
                    //}

                    if (m_object.managedObject.isValid)
                    {
                        if (HeEditorGUI.CsButton(HeEditorGUI.SpaceR(ref position, position.height)))
                        {
                            m_owner.gotoCB(new GotoCommand(m_object.managedObject));
                        }
                    }
                }

                switch ((Column)column)
                {
                    case Column.Type:
                        GUI.Label(position, typeName);
                        break;

                    case Column.Name:
                        GUI.Label(position, name);
                        break;

                    case Column.Size:
                        HeEditorGUI.Size(position, size);
                        break;

                    case Column.Address:
                        HeEditorGUI.Address(position, address);
                        break;

                    case Column.DontDestroyOnLoad:
                        GUI.Label(position, isDontDestroyOnLoad.ToString());
                        break;

                    case Column.IsPersistent:
                        GUI.Label(position, isPersistent.ToString());
                        break;

                    case Column.InstanceID:
                        GUI.Label(position, instanceId.ToString());
                        break;

#if HEAPEXPLORER_DISPLAY_REFS
                    case Column.ReferencesCount:
                        GUI.Label(position, m_referencesCount.ToString());
                        break;

                    case Column.ReferencedByCount:
                        GUI.Label(position, m_referencedByCount.ToString());
                        break;
#endif
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////////
        // GroupItem
        ///////////////////////////////////////////////////////////////////////////

        class GroupItem : AbstractItem
        {
            RichNativeType m_type;

#if HEAPEXPLORER_DISPLAY_REFS
            public override int referencesCount
            {
                get
                {
                    if (m_referencesCount == -1)
                    {
                        m_referencesCount = 0;
                        if (hasChildren)
                        {
                            for (int n = 0, nend = children.Count; n < nend; ++n)
                            {
                                var child = children[n] as AbstractItem;
                                if (child != null)
                                {
                                    var count = child.referencesCount;
                                    if (count > m_referencesCount)
                                        m_referencesCount = count;
                                }
                            }
                        }
                    }

                    return m_referencesCount;
                }
            }
            int m_referencesCount = -1;

            public override int referencedByCount
            {
                get
                {
                    if (m_referencedByCount == -1)
                    {
                        m_referencedByCount = 0;
                        if (hasChildren)
                        {
                            for (int n = 0, nend = children.Count; n < nend; ++n)
                            {
                                var child = children[n] as AbstractItem;
                                if (child != null)
                                {
                                    var count = child.referencedByCount;
                                    if (count > m_referencedByCount)
                                        m_referencedByCount = count;
                                }
                            }
                        }
                    }

                    return m_referencedByCount;
                }
            }
            int m_referencedByCount = -1;
#endif

            public override string typeName
            {
                get
                {
                    if (displayName != null && displayName.Length > 0)
                        return displayName;

                    return m_type.name;
                }
            }

            public override string name
            {
                get
                {
                    return "";
                }
            }

            public override int size
            {
                get
                {
                    if (m_size == -1)
                    {
                        m_size = 0;
                        if (hasChildren)
                        {
                            for (int n = 0, nend = children.Count; n < nend; ++n)
                            {
                                var child = children[n] as AbstractItem;
                                if (child != null)
                                    m_size += child.size;
                            }
                        }
                    }

                    return m_size;
                }
            }
            int m_size = -1;

            public override int count
            {
                get
                {
                    if (m_count == -1)
                    {
                        m_count = 0;
                        if (hasChildren)
                        {
                            m_count += children.Count;
                            for (int n = 0, nend = children.Count; n < nend; ++n)
                            {
                                var child = children[n] as AbstractItem;
                                if (child != null)
                                    m_count += child.count;
                            }
                        }
                    }

                    return m_count;
                }
            }
            int m_count = -1;

            public override System.UInt64 address
            {
                get
                {
                    return 0;
                }
            }

            public override bool isDontDestroyOnLoad
            {
                get
                {
                    return false;
                }
            }

            public override bool isPersistent
            {
                get
                {
                    return false;
                }
            }

            public override int instanceId
            {
                get
                {
                    return 0;
                }
            }

            public void Initialize(PackedMemorySnapshot snapshot, int managedTypeArrayIndex)
            {
                m_type = new RichNativeType(snapshot, managedTypeArrayIndex);
            }
            
            public override void OnGUI(Rect position, int column)
            {
                switch ((Column)column)
                {
                    case Column.Type:
                        HeEditorGUI.TypeName(position, typeName);
                        break;

                    case Column.Size:
                        HeEditorGUI.Size(position, size);
                        break;

                    case Column.Count:
                        GUI.Label(position, count.ToString());
                        break;
                }
            }
        }
    }
}