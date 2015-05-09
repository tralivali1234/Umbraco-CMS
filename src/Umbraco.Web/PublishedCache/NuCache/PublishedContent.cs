﻿using System;
using System.Collections.Generic;
using System.Linq;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Xml.XPath;
using Umbraco.Web.Models;
using Umbraco.Web.PublishedCache.NuCache.DataSource;
using Umbraco.Web.PublishedCache.NuCache.Navigable;

namespace Umbraco.Web.PublishedCache.NuCache
{
    // fixme - sort out PublishedContent class vs IPublishedContentOrMedia interface

    internal class PublishedContent : PublishedContentBase, IPublishedContentOrMedia
    {
        private readonly ContentNode _contentNode;
        private readonly ContentData _contentData;

        // fixme - these would never be refreshed?
        private readonly Lazy<string> _lazyCreatorName;
        private readonly Lazy<string> _lazyWriterName;

        private readonly IPublishedProperty[] _properties;
        private readonly string _urlName;
        private readonly bool _isPreviewing;

        #region Constructors

        public PublishedContent(ContentNode contentNode, ContentData contentData)
        {
            _contentNode = contentNode;
            _contentData = contentData;

            // fixme - these would never be refreshed?
            // fixme - inject the service
            var userService = ApplicationContext.Current.Services.UserService;
            _lazyCreatorName = new Lazy<string>(() => userService.GetProfileById(_contentNode.CreatorId).Name);
            _lazyWriterName = new Lazy<string>(() => userService.GetProfileById(_contentData.WriterId).Name);

            _urlName = _contentData.Name.ToUrlSegment();
            _isPreviewing = _contentData.Published == false;
            _properties = CreateProperties(this, contentData.Properties);
        }

        private static IPublishedProperty[] CreateProperties(PublishedContent content, IDictionary<string, object> values)
        {
            return content._contentNode.ContentType
                .PropertyTypes
                .Select(propertyType =>
                {
                    object value;
                    return values.TryGetValue(propertyType.PropertyTypeAlias, out value)
                        ? (IPublishedProperty) new Property(propertyType, content, value)
                        : (IPublishedProperty) new Property(propertyType, content);
                })
                .ToArray();
        }

        // (see ContentNode.CloneParent)
        public PublishedContent(ContentNode contentNode, PublishedContent origin)
        {
            _contentNode = contentNode;
            _contentData = origin._contentData;

            _lazyCreatorName = origin._lazyCreatorName;
            _lazyWriterName = origin._lazyWriterName;

            _urlName = origin._urlName;
            _isPreviewing = origin._isPreviewing;

            // here is the main benefit: we do not re-create properties so if anything
            // is cached locally, we share the cache - which is fine - if anything depends
            // on the tree structure, it should not be cached locally to begin with
            _properties = origin._properties;
        }

        // clone for previewing as draft a published content that is published and has no draft
        private PublishedContent(PublishedContent origin)
        {
            _contentNode = origin._contentNode;
            _contentData = origin._contentData;

            _lazyCreatorName = origin._lazyCreatorName;
            _lazyWriterName = origin._lazyWriterName;

            _urlName = origin._urlName;
            _isPreviewing = true;

            // clone properties so _isPreviewing is true
            _properties = origin._properties.Select(x => (IPublishedProperty) new Property((Property) x)).ToArray();
        }

        #endregion

        #region Get Content/Media for Parent/Children

        internal static Func<bool, int, IPublishedContent> GetContentByIdOverride;
        internal static Func<bool, int, IPublishedContent> GetMediaByIdOverride;

        private void EnsureGetContentById()
        {
            if (GetContentByIdOverride == null)
            {
                var cache = Facade.Current.ContentCache;
                GetContentByIdOverride = ((p, i) => cache.GetById(p, i));
            }
        }

        private void EnsureGetMediaById()
        {
            if (GetMediaByIdOverride == null)
            {
                var cache = Facade.Current.MediaCache;
                GetMediaByIdOverride = ((p, i) => cache.GetById(p, i));
            }
        }

        private IPublishedContent GetContentById(bool previewing, int id)
        {
            EnsureGetContentById();
            return GetContentByIdOverride(previewing, id);
        }

        private IEnumerable<IPublishedContent> GetContentByIds(bool previewing, IEnumerable<int> ids)
        {
            EnsureGetContentById();
            var content = ids.Select(x => GetContentByIdOverride(previewing, x));
            if (previewing == false)
                content = content.Where(x => x != null);
            return content;
        }

        private IPublishedContent GetMediaById(bool previewing, int id)
        {
            EnsureGetMediaById();
            return GetContentByIdOverride(previewing, id);
        }

        private IEnumerable<IPublishedContent> GetMediaByIds(bool previewing, IEnumerable<int> ids)
        {
            EnsureGetMediaById();
            return ids.Select(x => GetMediaByIdOverride(previewing, x));
        }

        #endregion

        #region IPublishedContent

        public override int Id { get { return _contentNode.Id; } }
        public override int DocumentTypeId { get { return _contentNode.ContentType.Id; } }
        public override string DocumentTypeAlias { get { return _contentNode.ContentType.Alias; } }
        public override PublishedItemType ItemType { get { return _contentNode.ContentType.ItemType; } }

        public override string Name { get { return _contentData.Name; } }
        public override int Level { get { return _contentNode.Level; } }
        public override string Path { get { return _contentNode.Path; } }
        public override int SortOrder { get { return _contentNode.SortOrder; } }
        public override Guid Version { get { return _contentData.Version; } }
        public override int TemplateId { get { return _contentData.TemplateId; } }

        public override string UrlName { get { return _urlName; } }

        public override DateTime CreateDate { get { return _contentNode.CreateDate; } }
        public override DateTime UpdateDate { get { return _contentData.VersionDate; } }

        public override int CreatorId { get { return _contentNode.CreatorId; } }
        public override string CreatorName { get { return _lazyCreatorName.Value; } }
        public override int WriterId { get { return _contentData.WriterId; } }
        public override string WriterName { get { return _lazyWriterName.Value; } }

        public override bool IsDraft { get { return _contentData.Published == false; } }

        public override IPublishedContent Parent
        {
            get
            {
                // have to use the "current" cache because a PublishedContent can be shared
                // amongst many snapshots and other content depend on the snapshots
                switch (_contentNode.ContentType.ItemType)
                {
                    case PublishedItemType.Content:
                        return GetContentById(_isPreviewing, _contentNode.ParentContentId);
                    case PublishedItemType.Media:
                        return GetMediaById(_isPreviewing, _contentNode.ParentContentId);
                    default:
                        throw new Exception("oops");
                }
            }
        }

        public override IEnumerable<IPublishedContent> Children
        {
            get
            {
                // have to use the "current" cache because a PublishedContent can be shared
                // amongst many snapshots and other content depend on the snapshots
                switch (_contentNode.ContentType.ItemType)
                {
                    // fixme - sort all the time vs. pre-sort?
                    case PublishedItemType.Content:
                        return GetContentByIds(_isPreviewing, _contentNode.ChildContentIds).OrderBy(x => x.SortOrder);
                    case PublishedItemType.Media:
                        return GetMediaByIds(_isPreviewing, _contentNode.ChildContentIds).OrderBy(x => x.SortOrder);
                    default:
                        throw new Exception("oops");
                }
            }
        }

        public override ICollection<IPublishedProperty> Properties { get { return _properties; } }

        public override IPublishedProperty GetProperty(string alias)
        {
            var index = _contentNode.ContentType.GetPropertyIndex(alias);
            var property = index < 0 ? null : _properties[index];
            return property;
        }

        public override IPublishedProperty GetProperty(string alias, bool recurse)
        {
            var property = GetProperty(alias);
            if (recurse == false) return property;

            var facade = Facade.Current;
            if (facade == null || facade.SnapshotCache == null)
                return base.GetProperty(alias, true);

            var key = ((Property) property).RecurseCacheKey;
            return (Property) facade.SnapshotCache.GetCacheItem(key, () => base.GetProperty(alias, true));
        }

        public override PublishedContentType ContentType
        {
            get { return _contentNode.ContentType; }
        }

        #endregion

        #region Internal

        IPublishedProperty[] IPublishedContentOrMedia.PropertiesArray { get { return _properties; } }

        int IPublishedContentOrMedia.ParentId { get { return _contentNode.ParentContentId; } }

        IList<int> IPublishedContentOrMedia.ChildIds { get { return _contentNode.ChildContentIds; } }

        INavigableContentType IPublishedContentOrMedia.NavigableContentType { get { return NavigableContentType.GetContentType(_contentNode.ContentType); } }

        bool IPublishedContentOrMedia.IsPreviewing { get { return _isPreviewing; } }

        private string _asPreviewingCacheKey;

        internal string AsPreviewingCacheKey
        {
            get { return _asPreviewingCacheKey ?? (_asPreviewingCacheKey = "NuCache.APR[" + Id + "]"); }
        }

        IPublishedContent IPublishedContentOrMedia.AsPreviewingModel()
        {
            if (_isPreviewing)
                return this;

            var facade = Facade.Current;
            var cache = facade == null ? null : facade.FacadeCache;
            if (cache == null) return new PublishedContent(this).CreateModel();
            return (IPublishedContent) cache.GetCacheItem(AsPreviewingCacheKey, () => new PublishedContent(this).CreateModel());
        }

        #endregion
    }
}