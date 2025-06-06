﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Roslyn.Utilities;
using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;

namespace Microsoft.VisualStudio.LanguageServices.Implementation;

/// <summary>
/// A singleton that tracks the open IVsWindowFrames and can report which documents are visible or active in a given <see cref="Workspace"/>.
/// Can be accessed via the <see cref="IDocumentTrackingService"/> as a workspace service.
/// </summary>
[Export]
internal sealed class VisualStudioActiveDocumentTracker : IVsSelectionEvents
{
    private readonly IThreadingContext _threadingContext;
    private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;

    /// <summary>
    /// The list of tracked frames. This can only be written by the UI thread, although can be read (with care) from any thread.
    /// </summary>
    private ImmutableList<FrameListener> _visibleFrames = [];

    /// <summary>
    /// The active IVsWindowFrame. This can only be written by the UI thread, although can be read (with care) from any thread.
    /// </summary>
    private IVsWindowFrame? _activeFrame;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public VisualStudioActiveDocumentTracker(
        IThreadingContext threadingContext,
        [Import(typeof(SVsServiceProvider))] IAsyncServiceProvider asyncServiceProvider,
        IVsEditorAdaptersFactoryService editorAdaptersFactoryService)
    {
        _threadingContext = threadingContext;
        _editorAdaptersFactoryService = editorAdaptersFactoryService;
        _threadingContext.RunWithShutdownBlockAsync(async cancellationToken =>
        {
            await _threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var monitorSelectionService = (IVsMonitorSelection?)await asyncServiceProvider.GetServiceAsync(typeof(SVsShellMonitorSelection)).ConfigureAwait(true);
            Assumes.Present(monitorSelectionService);

            // No need to track windows if we are shutting down
            cancellationToken.ThrowIfCancellationRequested();

            if (ErrorHandler.Succeeded(monitorSelectionService.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var value)))
            {
                if (value is IVsWindowFrame windowFrame)
                {
                    TrackNewActiveWindowFrame(windowFrame);
                }
            }

            monitorSelectionService.AdviseSelectionEvents(this, out var _);
        });
    }

    /// <summary>
    /// Raised when the set of window frames being tracked changes, which means the results from <see cref="TryGetActiveDocument"/> or <see cref="GetVisibleDocuments"/> may change.
    /// May be raised on any thread.
    /// </summary>
    public event EventHandler? DocumentsChanged;

    /// <summary>
    /// Returns the <see cref="DocumentId"/> of the active document in a given <see cref="Workspace"/>.
    /// </summary>
    public DocumentId? TryGetActiveDocument(Workspace workspace)
    {
        // ThisCanBeCalledOnAnyThread();

        // Fetch both fields locally. If there's a write between these, that's fine -- it might mean we
        // don't return the DocumentId for something we could have if _activeFrame isn't listed in _visibleFrames.
        // But given this API runs unsynchronized against the UI thread, even with locking the same could happen if somebody
        // calls just a fraction of a second early.
        var visibleFramesSnapshot = _visibleFrames;
        var activeFrameSnapshot = _activeFrame;

        if (activeFrameSnapshot == null || visibleFramesSnapshot.IsEmpty)
        {
            return null;
        }

        foreach (var listener in visibleFramesSnapshot)
        {
            if (listener.Frame == activeFrameSnapshot)
            {
                return listener.GetDocumentId(workspace);
            }
        }

        return null;
    }

    /// <summary>
    /// Get a read-only collection of the <see cref="DocumentId"/>s of all the visible documents in the given <see cref="Workspace"/>.
    /// </summary>
    public ImmutableArray<DocumentId> GetVisibleDocuments(Workspace workspace)
    {
        // ThisCanBeCalledOnAnyThread();

        var visibleFramesSnapshot = _visibleFrames;

        var ids = ArrayBuilder<DocumentId>.GetInstance(visibleFramesSnapshot.Count);

        foreach (var frame in visibleFramesSnapshot)
        {
            var documentId = frame.GetDocumentId(workspace);

            if (documentId != null)
            {
                ids.Add(documentId);
            }
        }

        return ids.ToImmutableAndFree();
    }

    public void TrackNewActiveWindowFrame(IVsWindowFrame frame)
    {
        _threadingContext.ThrowIfNotOnUIThread();

        Contract.ThrowIfNull(frame);

        _activeFrame = frame;

        var existingFrame = _visibleFrames.FirstOrDefault(f => f.Frame == frame);
        if (existingFrame == null)
        {
            _visibleFrames = _visibleFrames.Add(new FrameListener(this, frame));
        }
        else if (existingFrame.TextBuffer == null)
        {
            // If no text buffer is associated with existing frame, remove the existing frame and add the new one.
            // Note that we do not need to disconnect the existing frame here. It will get disconnected along with
            // the new frame whenever the document is closed or de-activated.
            _visibleFrames = _visibleFrames.Remove(existingFrame);
            _visibleFrames = _visibleFrames.Add(new FrameListener(this, frame));
        }

        this.DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveFrame(FrameListener frame)
    {
        _threadingContext.ThrowIfNotOnUIThread();

        if (frame.Frame == _activeFrame)
        {
            _activeFrame = null;
        }

        _visibleFrames = _visibleFrames.Remove(frame);

        this.DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    int IVsSelectionEvents.OnSelectionChanged(IVsHierarchy pHierOld, [ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSITEMID")] uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld, IVsHierarchy pHierNew, [ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSITEMID")] uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
        => VSConstants.E_NOTIMPL;

    int IVsSelectionEvents.OnElementValueChanged([ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSSELELEMID")] uint elementid, object varValueOld, object varValueNew)
    {
        _threadingContext.ThrowIfNotOnUIThread();

        // Process and track newly active document frame.
        // Note that sometimes we receive 'SEID_WindowFrame' instead of 'SEID_DocumentFrame'
        // for the newly active document. We ensure that we only process frames for documents
        // and not other tool windows by checking the frame type is 'WINDOWFRAMETYPE_Document'.
        if (elementid == (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame ||
            elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
        {
            // Remember the newly activated frame so it can be read from another thread.

            if (varValueNew is IVsWindowFrame frame &&
                ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Type, out var frameType)) &&
                (int)frameType == (int)__WindowFrameTypeFlags.WINDOWFRAMETYPE_Document)
            {
                TrackNewActiveWindowFrame(frame);
            }
        }

        return VSConstants.S_OK;
    }

    int IVsSelectionEvents.OnCmdUIContextChanged([ComAliasName("Microsoft.VisualStudio.Shell.Interop.VSCOOKIE")] uint dwCmdUICookie, [ComAliasName("Microsoft.VisualStudio.OLE.Interop.BOOL")] int fActive)
        => VSConstants.E_NOTIMPL;

    /// <summary>
    /// Listens to frame notifications for a visible frame. When the frame becomes invisible or closes,
    /// then it automatically disconnects.
    /// </summary>
    private class FrameListener : IVsWindowFrameNotify, IVsWindowFrameNotify2
    {
        public readonly IVsWindowFrame Frame;

        private readonly VisualStudioActiveDocumentTracker _documentTracker;
        private readonly uint _frameEventsCookie;

        internal ITextBuffer? TextBuffer { get; private set; }

        public FrameListener(VisualStudioActiveDocumentTracker service, IVsWindowFrame frame)
        {
            _documentTracker = service;

            _documentTracker._threadingContext.ThrowIfNotOnUIThread();

            this.Frame = frame;
            ((IVsWindowFrame2)frame).Advise(this, out _frameEventsCookie);

            TryInitializeTextBuffer();
        }

        /// <summary>
        /// Returns the current DocumentId for this window frame. Care must be made with this value, since "current" could change asynchronously as the document
        /// could be unregistered from a workspace.
        /// </summary>
        public DocumentId? GetDocumentId(Workspace workspace)
        {
            if (TextBuffer == null)
            {
                return null;
            }

            var textContainer = TextBuffer.AsTextContainer();
            return workspace.GetDocumentIdInCurrentContext(textContainer);
        }

        int IVsWindowFrameNotify.OnDockableChange(int fDockable)
            => VSConstants.S_OK;

        int IVsWindowFrameNotify.OnMove()
            => VSConstants.S_OK;

        int IVsWindowFrameNotify.OnShow(int fShow)
        {
            switch ((__FRAMESHOW)fShow)
            {
                case __FRAMESHOW.FRAMESHOW_WinShown when TextBuffer is null:
                    TryInitializeTextBuffer();
                    if (TextBuffer is not null)
                    {
                        // The current TextBuffer was initialized in the OnShow instead of being initialized in the
                        // constructor. For consumers, treat this the same way as when the active document changes.
                        _documentTracker.DocumentsChanged?.Invoke(_documentTracker, EventArgs.Empty);
                    }

                    return VSConstants.S_OK;

                case __FRAMESHOW.FRAMESHOW_WinClosed:
                case __FRAMESHOW.FRAMESHOW_WinHidden:
                case __FRAMESHOW.FRAMESHOW_TabDeactivated:
                    return Disconnect();
            }

            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify.OnSize()
            => VSConstants.S_OK;

        int IVsWindowFrameNotify2.OnClose(ref uint pgrfSaveOptions)
            => Disconnect();

        private void TryInitializeTextBuffer()
        {
            RoslynDebug.Assert(TextBuffer is null);

            _documentTracker._threadingContext.ThrowIfNotOnUIThread();

            if (ErrorHandler.Succeeded(Frame.GetProperty((int)__VSFPROPID12.VSFPROPID_IsDocDataInitialized, out var boxedIsDocDataInitialized)))
            {
                if (boxedIsDocDataInitialized is not bool isDocDataInitialized)
                {
                    // If we failed to unbox the bool. We'll try again on the next OnShow event.
                    return;
                }

                if (!isDocDataInitialized)
                {
                    // This document is not yet initialized. Defer initialization to the next OnShow event.
                    return;
                }
            }

            if (ErrorHandler.Succeeded(Frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var docData)) &&
                docData is IVsTextBuffer bufferAdapter)
            {
                TextBuffer = _documentTracker._editorAdaptersFactoryService.GetDocumentBuffer(bufferAdapter);
            }

            return;
        }

        private int Disconnect()
        {
            _documentTracker._threadingContext.ThrowIfNotOnUIThread();
            _documentTracker.RemoveFrame(this);

            if (_frameEventsCookie != VSConstants.VSCOOKIE_NIL)
            {
                return ((IVsWindowFrame2)Frame).Unadvise(_frameEventsCookie);
            }
            else
            {
                return VSConstants.S_OK;
            }
        }
    }
}
