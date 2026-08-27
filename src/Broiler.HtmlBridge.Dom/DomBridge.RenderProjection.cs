using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private sealed class RenderProjection(
        DomDocument document,
        IReadOnlyDictionary<DomElement, DomElement> projectedToSource)
    {
        public DomDocument Document { get; } = document;

        public DomElement? SourceFor(DomElement projected) =>
            projectedToSource.TryGetValue(projected, out var source) ? source : null;
    }

    // Set only while a projection is prepared. Render-only synthetic nodes must be
    // owned by this document; using _document would advance the live document version.
    private DomDocument? _renderProjectionDocument;

    // Projection element -> script-visible source element for geometry and stable
    // view-transition identity while projection transforms execute.
    private IReadOnlyDictionary<DomElement, DomElement>? _renderProjectionSources;

    private DomDocument NodeFactoryDocument => _renderProjectionDocument ?? _document;

    private DomElement ResolveRenderSource(DomElement element) =>
        _renderProjectionSources is not null &&
        _renderProjectionSources.TryGetValue(element, out var source)
            ? source
            : element;

    /// <summary>
    /// Builds an isolated renderer document. Importing directly into the new owner
    /// prevents clone construction from publishing mutations against the live document.
    /// </summary>
    private RenderProjection CreateRenderProjection()
    {
        var projectedDocument = new DomDocument();
        var projectedToSource = new Dictionary<DomElement, DomElement>(ReferenceEqualityComparer.Instance);
        var sourceToProjected = new Dictionary<DomElement, DomElement>(ReferenceEqualityComparer.Instance);

        if (_document.DocumentType is { } documentType)
            projectedDocument.AppendChild(projectedDocument.ImportNode(documentType));

        var sourceRoot = RenderedDocumentElement;
        if (sourceRoot is null)
            return new RenderProjection(projectedDocument, projectedToSource);

        var projectedRoot = (DomElement)projectedDocument.ImportNode(sourceRoot, deep: true);
        projectedDocument.AppendChild(projectedRoot);
        CopyRenderProjectionState(
            sourceRoot,
            projectedRoot,
            projectedToSource,
            sourceToProjected);
        RebindProjectedShadowState(sourceToProjected);

        var previousDocument = _renderProjectionDocument;
        var previousSources = _renderProjectionSources;
        _renderProjectionDocument = projectedDocument;
        _renderProjectionSources = projectedToSource;
        _zoomSpecifiedStyleCache.Clear();
        try
        {
            if (ZoomBakeActive)
                ApplyZoomSerializationStyles(projectedRoot, 1.0);
            ApplySerializationTransforms(projectedRoot);
            ApplyViewTransitionRendering(projectedRoot);
            ReflectRenderState(projectedRoot);
        }
        finally
        {
            _zoomSpecifiedStyleCache.Clear();
            ClearComputedPropsCache();
            _renderProjectionSources = previousSources;
            _renderProjectionDocument = previousDocument;
        }

        return new RenderProjection(projectedDocument, projectedToSource);
    }

    private void CopyRenderProjectionState(
        DomNode source,
        DomNode projected,
        Dictionary<DomElement, DomElement> projectedToSource,
        Dictionary<DomElement, DomElement> sourceToProjected)
    {
        if (source is DomElement sourceElement && projected is DomElement projectedElement)
        {
            projectedToSource[projectedElement] = sourceElement;
            sourceToProjected[sourceElement] = projectedElement;
            CopyBridgeRuntimeStateTo(sourceElement, projectedElement);
        }

        var sourceChildren = source.ChildNodes;
        var projectedChildren = projected.ChildNodes;
        for (var index = 0; index < sourceChildren.Count && index < projectedChildren.Count; index++)
        {
            CopyRenderProjectionState(
                sourceChildren[index],
                projectedChildren[index],
                projectedToSource,
                sourceToProjected);
        }
    }

    private void RebindProjectedShadowState(
        IReadOnlyDictionary<DomElement, DomElement> sourceToProjected)
    {
        foreach (var (source, projected) in sourceToProjected)
        {
            var sourceState = ShadowStateFor(source);
            var projectedState = ShadowStateFor(projected);

            if (sourceState.Root.TryGet(out var rawRoot) &&
                rawRoot is DomElement sourceRoot &&
                sourceToProjected.TryGetValue(sourceRoot, out var projectedRoot))
            {
                projectedState.Root.Set(projectedRoot);
            }

            if (sourceState.Host.TryGet(out var rawHost) &&
                rawHost is DomElement sourceHost &&
                sourceToProjected.TryGetValue(sourceHost, out var projectedHost))
            {
                projectedState.Host.Set(projectedHost);
            }
        }
    }

    private DomElement CloneSnapshotContentForRender(DomElement source)
    {
        var projectionDocument = _renderProjectionDocument ??
            throw new InvalidOperationException("Snapshot content can only be cloned during render projection.");
        var clone = (DomElement)projectionDocument.ImportNode(source, deep: true);
        CopyRuntimeStateForClonedSubtree(source, clone, deep: true);
        return clone;
    }
}
