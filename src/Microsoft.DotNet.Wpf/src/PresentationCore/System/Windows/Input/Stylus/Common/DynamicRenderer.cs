// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

//#define DEBUG_RENDERING_FEEDBACK
//
//
// Description:
//      DynamicRenderer PlugIn - Provides off (and on) app Dispatcher Inking support.
//
//

using System;
using System.Diagnostics;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading;
using System.Windows.Threading;
using MS.Utility;
using System.Windows.Ink;
using MS.Internal.Ink;
using System.Security;

using SR=MS.Internal.PresentationCore.SR;
using SRID=MS.Internal.PresentationCore.SRID;
    
namespace System.Windows.Input.StylusPlugIns
{
    /////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// [TBS]
    /// </summary>
    public class DynamicRenderer : StylusPlugIn
    {
        /////////////////////////////////////////////////////////////////////

        public class StrokeInfo
        {
            int _stylusId;
            int _startTime;
            int _lastTime;
            ContainerVisual _strokeCV;  // App thread rendering CV
            ContainerVisual _strokeRTICV; // real time input CV
            bool _seenUp; // Have we seen the stylusUp event yet?
            bool _isReset; // Was reset used to create this StrokeInfo?
            SolidColorBrush _fillBrush; // app thread based brushed
            DrawingAttributes _drawingAttributes;
            StrokeNodeIterator _strokeNodeIterator;
            double _opacity;
            DynamicRendererHostVisual   _strokeHV;  // App thread rendering HostVisual
            public StylusPointCollection allPoints = null;
            public bool canRender = false;
            public bool checkValid = true;
            public double totalCount = 0; 

            public StrokeInfo(DrawingAttributes drawingAttributes, int stylusDeviceId, int startTimestamp, DynamicRendererHostVisual hostVisual)
            {
                _stylusId = stylusDeviceId;
                _startTime = startTimestamp;
                _lastTime = _startTime;
                _drawingAttributes = drawingAttributes.Clone(); // stroke copy for duration of stroke.
                _strokeNodeIterator = new StrokeNodeIterator(_drawingAttributes);
                Color color = _drawingAttributes.Color;
                _opacity = _drawingAttributes.IsHighlighter ? 0 : (double)color.A / (double)StrokeRenderer.SolidStrokeAlpha;
                color.A = StrokeRenderer.SolidStrokeAlpha;
                
                // Set the brush to be used with this new stroke too (since frozen can be shared by threads)
                SolidColorBrush brush = new SolidColorBrush(color);
                brush.Freeze();
                _fillBrush = brush;
                _strokeHV = hostVisual;
                hostVisual.AddStrokeInfoRef(this); // Add ourselves as reference.
            }

            // Public props to access info
            public int StylusId 
            { 
                get { return _stylusId; }
            }
            public int StartTime 
            { 
                get { return _startTime; }
            }
            public int LastTime 
            { 
                get { return _lastTime; } 
                set { _lastTime = value; } 
            }
            public ContainerVisual StrokeCV 
            { 
                get { return _strokeCV; } 
                set { _strokeCV = value; } 
            }
            public ContainerVisual StrokeRTICV 
            { 
                get { return _strokeRTICV; } 
                set { _strokeRTICV = value; } 
            }
            public bool SeenUp 
            { 
                get { return _seenUp; } 
                set { _seenUp = value; } 
            }
            public bool IsReset
            { 
                get { return _isReset; }
                set { _isReset = value; }
            }
            internal StrokeNodeIterator StrokeNodeIterator
            { 
                get { return _strokeNodeIterator; }
                set 
                { 
                    if (value == null) 
                    {
                        throw new ArgumentNullException("StrokeNodeIterator");
                    }
                    _strokeNodeIterator = value; 
                }
            }
            public SolidColorBrush FillBrush
            { 
                get { return _fillBrush; } 
                set { _fillBrush = value; } 
            }
            public DrawingAttributes DrawingAttributes
            { 
                get { return _drawingAttributes; }
            }
            public double Opacity
            { 
                get { return _opacity; }
            }
            public DynamicRendererHostVisual StrokeHV
            { 
                get { return _strokeHV; }
            }

            // See if timestamp is part of this stroke.  Deals with tickcount wrapping.
            public bool IsTimestampWithin(int timestamp)
            {
                // If we've seen up use the start and end to figure out if timestamp
                // is between start and last.  Note that we need to deal with the 
                // times wrapping back to 0.
                if (SeenUp)
                {
                    if (StartTime < LastTime) // wrapping check
                    {
                        return ((timestamp >= StartTime) && (timestamp <= LastTime));
                    }
                    else // The timestamp wrapped back to zero
                    {
                        return ((timestamp >= StartTime) || (timestamp <= LastTime));
                    }
                }
                else
                {
                    return true; // everything is consider part of an open StrokeInfo.
                }
            }

            // See if a new timestamp adding at the end of this stroke.  Deals with tickcount wrapping.
            public bool IsTimestampAfter(int timestamp)
            {
                // If we've seen up then timestamp can't be after, otherwise do the check.
                // Note that we need to deal with the times wrapping (goes negative).
                if (!SeenUp)
                {
                    if (LastTime >= StartTime)
                    {
                        if (timestamp >= LastTime)
                        {
                            return true;
                        }
                        else
                        {
                            return ((LastTime > 0) && (timestamp < 0));  // true if we wrapped
                        }
                    }
                    else // The timestamp may have wrapped, see if greater than last time and less than start time
                    {
                        return timestamp >= LastTime && timestamp <= StartTime;
                    }
                }
                else
                {
                    return false; // Nothing can be after a closed StrokeInfo (see up).
                }
            }
}

        public class DynamicRendererHostVisual : HostVisual
        {
            internal bool InUse
            {
                get { return strokeInfoListEx.Count > 0; }
            }
            internal bool HasSingleReference
            {
                get { return strokeInfoListEx.Count == 1; }
            }
            internal void AddStrokeInfoRef(StrokeInfo si)
            {
                strokeInfoListEx.Add(si);
            }
            internal void RemoveStrokeInfoRef(StrokeInfo si)
            {
                strokeInfoListEx.Remove(si);
            }
            
            internal VisualTarget VisualTarget
            {
                get 
                { 
                    if (_visualTarget == null)
                    {
                        _visualTarget = new VisualTarget(this);
                        _visualTarget.RootVisual = new ContainerVisual();
                    }
                    return _visualTarget;
                }
            }
            
            VisualTarget       _visualTarget;
            List<StrokeInfo>   strokeInfoListEx = new List<StrokeInfo>();
        }
        
        /////////////////////////////////////////////////////////////////////

        /// <summary>
        /// [TBS] - On UIContext
        /// </summary>
        public DynamicRenderer() : base()
        {
            zeroSizedFrozenRectEx = new RectangleGeometry(new Rect(0,0,0,0));
            zeroSizedFrozenRectEx.Freeze();
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Reset will stop the current strokes being dynamically rendered 
        /// and start a new stroke with the packets passed in.  Specified StylusDevice 
        /// must be in down position when calling this method.
        /// Only call from application dispatcher.
        /// </summary>
        /// <param name="stylusDevice"></param>
        /// <param name="stylusPoints"></param>
        public virtual void Reset(StylusDevice stylusDevice, StylusPointCollection stylusPoints)
        {
            // NOTE: stylusDevice == null means the mouse device.

            // Nothing to do if root visual not queried or not hookup up to element yet.
            if (mainContainerVisualEx == null || applicationDispatcherEx == null || !IsActiveForInput)
                return;
            
            // Ensure on UIContext.
            applicationDispatcherEx.VerifyAccess();

            // Make sure the stylusdevice specified (or mouse if null stylusdevice) is currently in 
            // down state!
            bool inAir = (stylusDevice != null) ? 
                            stylusDevice.InAir : 
                            Mouse.PrimaryDevice.LeftButton == MouseButtonState.Released;
            
            if (inAir)
            {
                throw new ArgumentException(SR.Get(SRID.Stylus_MustBeDownToCallReset), "stylusDevice");
            }

            // Avoid reentrancy due to lock() call.
            using(applicationDispatcherEx.DisableProcessing())
            {
                lock(siLockEx)
                {
                    AbortAllStrokes(); // stop any current inking strokes

                    // Now create new si and insert it in the list.
                    StrokeInfo si = new StrokeInfo(DrawingAttributes, 
                                                   (stylusDevice != null) ? stylusDevice.Id : 0, 
                                                   Environment.TickCount, GetCurrentHostVisual());
                    strokeInfoListEx.Add(si);
                    si.IsReset = true;

                    if (stylusPoints != null)
                    {
                        RenderPackets(stylusPoints, si); // do this inside of lock to make sure this renders first.
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On app Dispatcher
        /// </summary>
        public virtual Visual RootVisual
        {
            get
            {
                // NOTE: We don't create any visuals (real time or non real time) until someone
                //  queries for this property since we can't display anything until this is done and
                // they hook the returned visual up to their visual tree.
                if (mainContainerVisualEx == null)
                {
                    CreateInkingVisuals(); // ensure at least the app dispatcher visuals are created.
                }
                return mainContainerVisualEx;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On app Dispatcher
        /// </summary>
        protected override void OnAdded()
        {
            // Grab the dispatcher we're hookup up to.
            applicationDispatcherEx = Element.Dispatcher;
            
            // If we are active for input, make sure we create the real time inking thread
            // and visuals if needed.
            if (IsActiveForInput)
            {
                CreateRealTimeVisuals();  // Transitions to inking thread.
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On app dispatcher
        /// </summary>
        protected override void OnRemoved()
        {
            // Make sure we destroy any real time visuals and thread when removed.
            DestroyRealTimeVisuals();
            applicationDispatcherEx = null; // removed from tree.
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On UIContext
        /// </summary>
        protected override void OnIsActiveForInputChanged()
        {
            // We only want to keep our real time inking thread references around only
            // when we need them.  If not enabled for input then we don't need them.
            if (IsActiveForInput)
            {
                // Make sure we create the real time inking visuals if we in tree.
                CreateRealTimeVisuals();  // Transitions to inking thread.
            }
            else
            {
                DestroyRealTimeVisuals();
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On pen threads or app thread
        /// </summary>
        protected override void OnStylusEnter(RawStylusInput rawStylusInput, bool confirmed)
        {
            HandleStylusEnterLeave(rawStylusInput, true, confirmed);
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On pen threads or app thread
        /// </summary>
        protected override void OnStylusLeave(RawStylusInput rawStylusInput, bool confirmed)
        {
            HandleStylusEnterLeave(rawStylusInput, false, confirmed);
        }

        public virtual void HandleStylusEnterLeave(RawStylusInput rawStylusInput, bool isEnter, bool isConfirmed)
        {
            // See if we need to abort a stroke due to entering or leaving within a stroke.
            if (isConfirmed)
            {
                StrokeInfo si = FindStrokeInfo(rawStylusInput.Timestamp);

                if (si != null)
                {
                    if (rawStylusInput.StylusDeviceId == si.StylusId)
                    {
                        if ((isEnter && (rawStylusInput.Timestamp > si.StartTime)) ||
                            (!isEnter && !si.SeenUp))
                        {
                            // abort this stroke.
                            TransitionStrokeVisuals(si, true);
                        }
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On UIContext
        /// </summary>
        protected override void OnEnabledChanged()
        {
            // If going disabled cancel all real time strokes.  We won't be getting any more
            // events.
            if (!Enabled)
            {
                AbortAllStrokes();
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected override void OnStylusDown(RawStylusInput rawStylusInput)
        {
            // Only allow inking if someone has queried our RootVisual.
            if (mainContainerVisualEx != null)
            {
                StrokeInfo si;
                
                lock(siLockEx)
                {
                    si = FindStrokeInfo(rawStylusInput.Timestamp);
                   
                    // If we find we are already in the middle of stroke then bail out.
                    // Can only ink with one stylus at a time.
                    if (si != null)
                    {
                        return; 
                    }

                    si = new StrokeInfo(DrawingAttributes, rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp, GetCurrentHostVisual());
                    si.allPoints = new StylusPointCollection(rawStylusInput.GetStylusPoints().Description);
                    StylusPointCollection upCollectionPoints = rawStylusInput.GetStylusPoints();
                    if (null != upCollectionPoints && 0 != upCollectionPoints.Count && null != si.allPoints)
                    {
                        si.allPoints.Add(upCollectionPoints);
                    }
                    strokeInfoListEx.Add(si);
                }

                //foreach (var ps in si.allPoints)
                //{
                //    //Trace.WriteLine("hjc25 down id: " + si.StylusId + "X: " + ps.X + "Y: " + ps.Y);
                //}

                rawStylusInput.NotifyWhenProcessed(si);
                //RenderPackets(rawStylusInput.GetStylusPoints(), si);
            }
        }

        private double pointLength(StylusPoint p1, StylusPoint p2)
        {

            double dLength = Math.Sqrt(((p1.X - p2.X) * (p1.X - p2.X)) + ((p1.Y - p2.Y) * (p1.Y - p2.Y)));
            return dLength;
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected override void OnStylusMove(RawStylusInput rawStylusInput)
        {
            // Only allow inking if someone has queried our RootVisual.
            if (mainContainerVisualEx != null)
            {
                StrokeInfo si = FindStrokeInfo(rawStylusInput.Timestamp);

                if (si != null && (si.StylusId == rawStylusInput.StylusDeviceId))
                {
                    // We only render packets that are in the proper order due to
                    // how our incremental rendering uses the last point to continue
                    // the path geometry from.
                    // NOTE: We also update the LastTime value here too
                    if (si.IsTimestampAfter(rawStylusInput.Timestamp))
                    {
                        si.LastTime = rawStylusInput.Timestamp;
                        StylusPointCollection upCollectionPoints = rawStylusInput.GetStylusPoints();

                        if (si.canRender)
                        {
                            RenderPackets(rawStylusInput.GetStylusPoints(), si);

                        }

                        if (null != upCollectionPoints && 0 != upCollectionPoints.Count && null != si.allPoints)
                        {
                            int timeSpan = si.LastTime - si.StartTime;
                            //foreach (var ps in upCollectionPoints)
                            //{
                            //    //Trace.WriteLine("hjc25 move id: " + si.StylusId + "X: " + ps.X + "Y: " + ps.Y + " timeSPan:" + timeSpan);
                            //}


                            if (!si.canRender)
                            {
                                //进行校验
                                if (si.checkValid)
                                {
                                    //获取si.allPoint的最后一个点
                                    int psLength = si.allPoints.Count;
                                    StylusPoint pStart = new StylusPoint(si.allPoints[psLength - 1].X, si.allPoints[psLength - 1].Y);

                                    //foreach (var psTmp in upCollectionPoints)
                                    //{
                                    //    //Trace.WriteLine("hjcs id: checkcollection: " + si.StylusId + "X: " + psTmp.X + "Y: " + psTmp.Y + " timeSpan" + timeSpan);
                                    //}
                                    //同新增点进行比较判断
                                    foreach (var ps in upCollectionPoints)
                                    {
                                        //Trace.WriteLine("hjcs id: continue start: " + si.StylusId + "X: " + pStart.X + "Y: " + pStart.Y + " timeSpan" + timeSpan);
                                        //Trace.WriteLine("hjcs id: continue end: " + si.StylusId + "X: " + ps.X + "Y: " + ps.Y + " timeSpan" + timeSpan);
                                        double dLength = pointLength(pStart, ps);
                                        if (!si.canRender)
                                        {
                                            //相邻点的长度大于100时为无效点
                                            if (dLength <= 200)
                                            {
                                                pStart.X = ps.X;
                                                pStart.Y = ps.Y;
                                                si.allPoints.Add(ps);
                                                continue;
                                            }
                                            else
                                            {
                                                si.allPoints.Clear();
                                                Trace.WriteLine("hjcs id: clear: " + si.StylusId + "X: " + ps.X + "Y: " + ps.Y + " timeSpan" + timeSpan);
                                                si.allPoints.Add(ps);
                                                si.canRender = true;
                                                si.checkValid = false;
                                            }
                                        }
                                        else
                                        {
                                            si.allPoints.Add(ps);
                                        }
                                    }
                                }

                                if (timeSpan >= 35)
                                {
                                    Trace.WriteLine("hjc id: timeSpan: " + timeSpan);
                                    si.checkValid = false;
                                    si.canRender = true;
                                }

                                if (si.canRender)
                                {
                                    RenderPackets(si.allPoints, si);
                                }
                            }
                            else
                            {
                                //foreach (var psTmp in upCollectionPoints)
                                //{
                                //    //Trace.WriteLine("hjcs id: continue use: " + si.StylusId + "X: " + psTmp.X + "Y: " + psTmp.Y + " timeSpan" + timeSpan);
                                //}

                                si.allPoints.Add(upCollectionPoints);
                            }

                        }
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected override void OnStylusUp(RawStylusInput rawStylusInput)
        {
            // Only allow inking if someone has queried our RootVisual.
            if (mainContainerVisualEx != null)
            {
                StrokeInfo si = FindStrokeInfo(rawStylusInput.Timestamp);

                if (si != null && 
                    ((si.StylusId == rawStylusInput.StylusDeviceId) ||
                     (rawStylusInput.StylusDeviceId == 0 && 
                      (si.IsReset || 
                      (si.IsTimestampAfter(rawStylusInput.Timestamp) && IsStylusUp(si.StylusId))))))
                {
                    si.SeenUp = true;
                    si.LastTime = rawStylusInput.Timestamp;
                    rawStylusInput.NotifyWhenProcessed(si);
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected virtual void stylusUpNormalProcess(StylusPointCollection stylusPoints)
        {

        }

        public virtual bool IsStylusUp(int stylusId)
        {
            TabletDeviceCollection tabletDevices = Tablet.TabletDevices;
            for (int i=0; i<tabletDevices.Count; i++)
            {
                TabletDevice tabletDevice = tabletDevices[i];
                for (int j=0; j<tabletDevice.StylusDevices.Count; j++)
                {
                    StylusDevice stylusDevice = tabletDevice.StylusDevices[j];
                    if (stylusId == stylusDevice.Id)
                        return stylusDevice.InAir;
                }
            }
            
            return true; // not found so must be up.
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        public virtual void OnRenderComplete()
        {
            StrokeInfo si = renderCompleteStrokeInfoEx;
            Debug.Assert(si!=null);  // should never get here unless we are transitioning a stroke.
            
            if (si != null)
            {
                // See if we are done transitioning this stroke!!
                if (si.StrokeHV.Clip == null)
                {
                    TransitionComplete(si);
                    renderCompleteStrokeInfoEx = null;
                }
                else
                {
                    // Wait for real time visual to be removed and updated.
                    RemoveDynamicRendererVisualAndNotifyWhenDone(si);
                }
            }
        }

        public virtual void RemoveDynamicRendererVisualAndNotifyWhenDone(StrokeInfo si)
        {
            if (si != null)
            {
                DynamicRendererThreadManager renderingThread = renderingThreadEx; // Keep it alive
                if (renderingThread != null)
                {
                    // We are being called by the main UI thread, so marshal over to
                    // the inking thread before cleaning up the stroke visual.
                    renderingThread.ThreadDispatcher.BeginInvoke(DispatcherPriority.Send,
                    (DispatcherOperationCallback)delegate(object unused)
                    {
                        if (si.StrokeRTICV != null)
                        {
                            // Now wait till this is rendered and then notify UI thread.
                            if (onDRThreadRenderCompleteEx == null)
                            {
                                onDRThreadRenderCompleteEx = new EventHandler(OnDRThreadRenderComplete);
                            }

                            // Add to list to transact.
                            renderCompleteDRThreadStrokeInfoListEx.Enqueue(si);
                            
                            // See if we are already waiting for a removed stroke to be rendered.
                            // If we aren't then remove visuals and wait for it to be rendered.
                            // Otherwise we'll do the work when the current stroke has been removed.
                            if (!waitingForDRThreadRenderCompleteEx)
                            {
                                ((ContainerVisual)si.StrokeHV.VisualTarget.RootVisual).Children.Remove(si.StrokeRTICV);
                                si.StrokeRTICV = null;

                                // hook up render complete notification for one time then unhook.
                                MediaContext.From(renderingThread.ThreadDispatcher).RenderComplete += onDRThreadRenderCompleteEx;
                                waitingForDRThreadRenderCompleteEx = true;
                            }
                        }
                        else
                        {
                            // Nothing to transition so just say we're done!
                            NotifyAppOfDRThreadRenderComplete(si);
                        }
                        
                        return null;
                    },
                    null);
                }
            }
        }


        public virtual void NotifyAppOfDRThreadRenderComplete(StrokeInfo si)
        {
            Dispatcher dispatcher = applicationDispatcherEx;
            if (dispatcher != null)
            {
                // We are being called by the inking thread, so marshal over to
                // the UI thread before handling the StrokeInfos that are done rendering.
                dispatcher.BeginInvoke(DispatcherPriority.Send,
                (DispatcherOperationCallback)delegate(object unused)
                {
                    // See if this is the one we are doing a full transition for.
                    if (si == renderCompleteStrokeInfoEx)
                    {
                        if (si.StrokeHV.Clip != null)
                        {
                            si.StrokeHV.Clip = null;
                            NotifyOnNextRenderComplete();
                        }
                        else
                        {
                            Debug.Assert(waitingForRenderCompleteEx, "We were expecting to be waiting for a RenderComplete to call our OnRenderComplete, we might never reset and get flashing strokes from here on out");
                            TransitionComplete(si); // We're done
                        }
                    }
                    else
                    {
                        TransitionComplete(si); // We're done
                    }
                    return null;
                },
                null);
            }
        }


        public virtual void OnDRThreadRenderComplete(object sender, EventArgs e)
        {
            DynamicRendererThreadManager drThread = renderingThreadEx;
            Dispatcher drDispatcher = null;
                       
            // Remove RenderComplete hook.
            if (drThread != null)
            {
                drDispatcher = drThread.ThreadDispatcher;
                
                if (drDispatcher != null)
                {
                    if (renderCompleteDRThreadStrokeInfoListEx.Count > 0)
                    {
                        StrokeInfo si = renderCompleteDRThreadStrokeInfoListEx.Dequeue();
                        NotifyAppOfDRThreadRenderComplete(si);
                    }

                    // If no other queued up transitions, then remove event listener.
                    if (renderCompleteDRThreadStrokeInfoListEx.Count == 0)
                    {
                        // First unhook event handler
                        MediaContext.From(drDispatcher).RenderComplete -= onDRThreadRenderCompleteEx;
                        waitingForDRThreadRenderCompleteEx = false;
                    }
                    else
                    {
                        // Process next waiting one.  Note we don't remove till removed processed.
                        StrokeInfo siNext = renderCompleteDRThreadStrokeInfoListEx.Peek();
                        if (siNext.StrokeRTICV != null)
                        {
                            // Post this back to our thread to make sure we return from the
                            // this render complete call first before queuing up the next.
                            drDispatcher.BeginInvoke(DispatcherPriority.Send,
                            (DispatcherOperationCallback)delegate(object unused)
                            {
                                ((ContainerVisual)siNext.StrokeHV.VisualTarget.RootVisual).Children.Remove(siNext.StrokeRTICV);
                                siNext.StrokeRTICV = null;
                                return null;
                            },
                            null);
                        }
                    }
                }
            }
        }


        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected override void OnStylusDownProcessed(object callbackData, bool targetVerified)
        {
            StrokeInfo si = callbackData as StrokeInfo;

            if (si == null)
                return;
            
            // See if we need to abort this stroke or reset the HostVisual clipping rect to null.
            if (!targetVerified)
            {
                TransitionStrokeVisuals(si, true);
            }
        }
        
        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected override void OnStylusUpProcessed(object callbackData, bool targetVerified)
        {
            StrokeInfo si = callbackData as StrokeInfo;

            if (si == null)
                return;

            // clean up stroke visuals (and move to transitional VisualTarget as needed)
            TransitionStrokeVisuals(si, !targetVerified);
        }

        public virtual void OnInternalRenderComplete(object sender, EventArgs e)
        {
            // First unhook event handler
            MediaContext.From(applicationDispatcherEx).RenderComplete -= onRenderCompleteEx;
            waitingForRenderCompleteEx = false;
            
            // Make sure lock() doesn't cause reentrancy.
            using(applicationDispatcherEx.DisableProcessing())
            {
                // Now notify event happened.
                OnRenderComplete();
            }
        }


        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        public virtual void NotifyOnNextRenderComplete()
        {
            // Nothing to do if not hooked up to plugin collection.
            if (applicationDispatcherEx == null)
                return;

            // Ensure on application Dispatcher.
            applicationDispatcherEx.VerifyAccess();

            if (onRenderCompleteEx == null)
            {
                onRenderCompleteEx = new EventHandler(OnInternalRenderComplete);
            }

            if (!waitingForRenderCompleteEx)
            {
                // hook up render complete notification for one time then unhook.
                MediaContext.From(applicationDispatcherEx).RenderComplete += onRenderCompleteEx;
                waitingForRenderCompleteEx = true;
            }
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected virtual void OnDraw(  DrawingContext drawingContext, 
                                        StylusPointCollection stylusPoints, 
                                        Geometry geometry, 
                                        Brush fillBrush)
        {
            if (drawingContext == null)
            {
                throw new ArgumentNullException("drawingContext");
            }
            drawingContext.DrawGeometry(fillBrush, null, geometry);
        }
        
        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected virtual void OnDrawingAttributesReplaced()
        {
        }
        
        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Retrieves the Dispatcher for the thread used for rendering dynamic strokes
        /// when receiving data from the stylus input thread(s).
        /// </summary>
        protected virtual Dispatcher GetDispatcher()
        {
            return renderingThreadEx != null ? renderingThreadEx.ThreadDispatcher : null;
        }

        /////////////////////////////////////////////////////////////////////

        protected virtual void RenderPackets(StylusPointCollection stylusPoints,  StrokeInfo si)
        {
            // If no points or not hooked up to element then do nothing.
            if (stylusPoints.Count == 0 || applicationDispatcherEx == null)
                return;

            // Get a collection of ink nodes built from the new stylusPoints.
            si.StrokeNodeIterator = si.StrokeNodeIterator.GetIteratorForNextSegment(stylusPoints);
            if (si.StrokeNodeIterator != null)
            {
                // Create a PathGeometry representing the contour of the ink increment
                Geometry strokeGeometry;
                Rect bounds;
                StrokeRenderer.CalcGeometryAndBounds(si.StrokeNodeIterator,
                                                     si.DrawingAttributes,
#if DEBUG_RENDERING_FEEDBACK
                                                     null, //debug dc
                                                     0d,   //debug feedback size
                                                     false,//render debug feedback
#endif
                                                     false, //calc bounds
                                                     out strokeGeometry,
                                                     out bounds);
        
                // If we are called from the app thread we can just stay on it and render to that
                // visual tree.  Otherwise we need to marshal over to our inking thread to do our work.
                if (applicationDispatcherEx.CheckAccess())
                {
                    // See if we need to create a new container visual for the stroke.
                    if (si.StrokeCV == null)
                    {
                        // Create new container visual for this stroke and add our incremental rendering visual to it.
                        si.StrokeCV = new ContainerVisual();

                        // two incrementally rendered stroke segments blend together
                        // at the rendering point location, thus the alpha value at those locations are higher than the set value.
                        // This is like you draw two strokes using static rendeer and the intersection part becomes darker.
                        // Set the opacity of the RootContainerVisual of the whole incremental stroke as color.A/255.0 and override
                        // the alpha value of the color we send to mil for rendering.
                        if (!si.DrawingAttributes.IsHighlighter)
                        {
                            si.StrokeCV.Opacity = si.Opacity;
                        }
                        mainRawInkContainerVisualEx.Children.Add(si.StrokeCV);
                    }
                    
                    // Create new visual and render the geometry into it
                    DrawingVisual visual = new DrawingVisual();
                    DrawingContext drawingContext = visual.RenderOpen();
                    try
                    {
                        OnDraw(drawingContext, stylusPoints, strokeGeometry, si.FillBrush);
                    }
                    finally
                    {
                        drawingContext.Close();
                    }
                    
                    // Now add it to the visual tree (making sure we still have StrokeCV after
                    // onDraw called above).
                    if (si.StrokeCV != null)
                    {
                        si.StrokeCV.Children.Add(visual);
                    }
                }
                else
                {
                    DynamicRendererThreadManager renderingThread = renderingThreadEx; // keep it alive
                    Dispatcher drDispatcher = renderingThread != null ? renderingThread.ThreadDispatcher : null;

                    // Only try to render if we get a ref on the rendering thread.
                    if (drDispatcher != null)
                    {
                        // We are on a pen thread so marshal this call to our inking thread.
                        drDispatcher.BeginInvoke(DispatcherPriority.Send,
                        (DispatcherOperationCallback) delegate(object unused)
                        {
                            SolidColorBrush fillBrush = si.FillBrush;

                            // Make sure this stroke is not aborted
                            if (fillBrush != null)
                            {
                                // See if we need to create a new container visual for the stroke.
                                if (si.StrokeRTICV == null)
                                {
                                    // Create new container visual for this stroke and add our incremental rendering visual to it.
                                    si.StrokeRTICV = new ContainerVisual();

                                    // two incrementally rendered stroke segments blend together
                                    // at the rendering point location, thus the alpha value at those locations are higher than the set value.
                                    // This is like you draw two strokes using static rendeer and the intersection part becomes darker.
                                    // Set the opacity of the RootContainerVisual of the whole incremental stroke as color.A/255.0 and override
                                    // the alpha value of the color we send to mil for rendering.
                                    if (!si.DrawingAttributes.IsHighlighter)
                                    {
                                        si.StrokeRTICV.Opacity = si.Opacity;
                                    }
                                    ((ContainerVisual)si.StrokeHV.VisualTarget.RootVisual).Children.Add(si.StrokeRTICV);
                                }
                                
                                // Create new visual and render the geometry into it
                                DrawingVisual visual = new DrawingVisual();
                                DrawingContext drawingContext = visual.RenderOpen();
                                try
                                {
                                    OnDraw(drawingContext, stylusPoints, strokeGeometry, fillBrush);
                                }
                                finally
                                {
                                    drawingContext.Close();
                                }
                                // Add it to the visual tree
                                si.StrokeRTICV.Children.Add(visual);
                            }
                
                            return null;
                        },
                        null);
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////

        protected virtual void AbortAllStrokes()
        {
            lock(siLockEx)
            {
                while (strokeInfoListEx.Count > 0)
                {
                    TransitionStrokeVisuals(strokeInfoListEx[0], true);
                }
            }
        }


        // The starting point for doing flicker free rendering when transitioning a real time
        // stroke from the DynamicRenderer thread to the application thread.
        //
        // There's a multi-step process to do this.  We now alternate between the two host visuals
        // to do the transtion work.  Only one HostVisual can be doing a full transition at one time.
        // When ones busy the other one reverts back to just removing the real time visual without
        // doing the full flicker free work.
        //
        // Here's the steps for a full transition using a Single DynamicRendererHostVisual:
        //
        // 1) [UI Thread] Set HostVisual.Clip = zero rect and then wait for render complete of that
        // 2) [UI Thread] On RenderComplete gets hit - Call over to DR thread to remove real time visual
        // 3) [DR Thread] Removed real time stroke visual and wait for rendercomplete of that
        // 4) [DR Thread] On RenderComplete of that call back over to UI thread to let it know that's done
        // 5) [UI Thread] Reset HostVisual.Clip = null and wait for render complete of that
        // 6) [UI Thread] On rendercomplete - we done.  Mark this HostVisual as free.
        //
        // In the case of another stroke coming through before a previous transition has completed
        // then basically instead of starting with step 1 we jump to step 2 and when then on step 5
        // we mark the HostVisual free and we are done.
        //
        protected virtual void TransitionStrokeVisuals(StrokeInfo si, bool abortStroke)
        {
            // Make sure we don't get any more input for this stroke.
            RemoveStrokeInfo(si);
            
            // remove si visuals and this si
            if (si.StrokeCV != null)
            {
                if (mainRawInkContainerVisualEx != null)
                {
                    mainRawInkContainerVisualEx.Children.Remove(si.StrokeCV);
                }
                si.StrokeCV = null;
            }

            si.FillBrush = null;

            // Nothing to do if we've destroyed our host visuals.
            if (rawInkHostVisual1Ex == null)
                return;

            bool doRenderComplete = false;
            
            // See if we can do full transition (only when none in progress and not abort)
            if (!abortStroke && renderCompleteStrokeInfoEx == null)
            {
                // make sure lock does not cause reentrancy on application thread!
                using (applicationDispatcherEx.DisableProcessing())
                {
                    lock (siLockEx)
                    {
                        // We can transition the host visual only if a single reference is on it.
                        if (si.StrokeHV.HasSingleReference)
                        {
                            Debug.Assert(si.StrokeHV.Clip == null);
                            si.StrokeHV.Clip = zeroSizedFrozenRectEx;
                            Debug.Assert(renderCompleteStrokeInfoEx == null);
                            renderCompleteStrokeInfoEx = si;
                            doRenderComplete = true;
                        }
                    }
                }
            }

            if (doRenderComplete)
            {
                NotifyOnNextRenderComplete();
            }
            else
            {
                // Just wait to dynamic rendering thread is updated then we're done.
                RemoveDynamicRendererVisualAndNotifyWhenDone(si);
            }
        }

        // Figures out the correct DynamicRenderHostVisual to use.
        protected virtual DynamicRendererHostVisual GetCurrentHostVisual()
        {
            // Find which of the two host visuals to use as current.
            if (currentHostVisualEx == null)
            {
                currentHostVisualEx = rawInkHostVisual1Ex;
            }
            else
            {
                HostVisual transitioningHostVisual = renderCompleteStrokeInfoEx != null ?
                                                        renderCompleteStrokeInfoEx.StrokeHV : null;

                if (currentHostVisualEx.InUse)
                {
                    if (currentHostVisualEx == rawInkHostVisual1Ex)
                    {
                        if (!rawInkHostVisual2Ex.InUse || rawInkHostVisual1Ex == transitioningHostVisual)
                        {
                            currentHostVisualEx = rawInkHostVisual2Ex;
                        }
                    }
                    else
                    {
                        if (!rawInkHostVisual1Ex.InUse || rawInkHostVisual2Ex == transitioningHostVisual)
                        {
                            currentHostVisualEx = rawInkHostVisual1Ex;
                        }
                    }
                }
            }
            return currentHostVisualEx;
        }


        // Removes ref from DynamicRendererHostVisual.
        protected virtual void TransitionComplete(StrokeInfo si)
        {
            // make sure lock does not cause reentrancy on application thread!
            using(applicationDispatcherEx.DisableProcessing())
            {
                lock(siLockEx)
                {
                    si.StrokeHV.RemoveStrokeInfoRef(si);
                }
            }
        }

        protected virtual void RemoveStrokeInfo(StrokeInfo si)
        {
            lock(siLockEx)
            {
                strokeInfoListEx.Remove(si);
            }
        }

        private StrokeInfo FindStrokeInfo(int timestamp)
        {
            lock(siLockEx)
            {
                for (int i=0; i < strokeInfoListEx.Count; i++)
                {
                    StrokeInfo siCur = strokeInfoListEx[i];
                    
                    if (siCur.IsTimestampWithin(timestamp))
                    {
                        return siCur;
                    }
                }
            }
            
            return null;
        }

        /////////////////////////////////////////////////////////////////////

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On UIContext
        /// </summary>
        public virtual DrawingAttributes DrawingAttributes
        {
            get // called from two UIContexts
            {
                return drawAttrsSourceEx;
            }
            set // (called in UIContext)
            {
                if (value == null)
                    throw new ArgumentNullException("value");
                
                drawAttrsSourceEx = value;

                OnDrawingAttributesReplaced();
            }
        }

        public virtual void CreateInkingVisuals()
        {
            if (mainContainerVisualEx == null)
            {
                mainContainerVisualEx = new ContainerVisual();
                mainRawInkContainerVisualEx = new ContainerVisual();
                mainContainerVisualEx.Children.Add(mainRawInkContainerVisualEx);
            }
            
            if (IsActiveForInput)
            {
                // Make sure lock() doesn't cause reentrancy.
                using (Element.Dispatcher.DisableProcessing())
                {
                    CreateRealTimeVisuals();
                }
            }
        }

        /// <summary>
        /// Create the visual target
        /// This method is called from the application context
        /// </summary>
        public virtual void CreateRealTimeVisuals()
        {
            // Only create if we have a root visual and have not already created them.
            if (mainContainerVisualEx != null && rawInkHostVisual1Ex == null)
            {
                // Create new VisualTarget and hook up in apps visuals under element.
                rawInkHostVisual1Ex = new DynamicRendererHostVisual();
                rawInkHostVisual2Ex = new DynamicRendererHostVisual();
                currentHostVisualEx = null;  // Pick a new current HostVisual on first stylus input.
                mainContainerVisualEx.Children.Add(rawInkHostVisual1Ex);
                mainContainerVisualEx.Children.Add(rawInkHostVisual2Ex);
                // NOTE: Do the work later if perf is bad hooking up VisualTargets on StylusDown...
                
                // Guarentee that objects are valid when on the DR thread below.
                //DynamicRendererHostVisual[] myArgs = new DynamicRendererHostVisual[2] { rawInkHostVisual1Ex, rawInkHostVisual2Ex };

                // Do this last since we can be reentrant on this call and we want to set
                // things up so we are all set except for the real time thread visuals which 
                // we set up on first usage.
                renderingThreadEx = DynamicRendererThreadManager.GetCurrentThreadInstance();

                /*
                // We are being called by the main UI thread, so invoke a call over to
                // the inking thread to create the visual targets.
                // NOTE: Since input rendering uses the same priority we are guanenteed
                //       that this will be processed before any input will try to be rendererd.
                renderingThreadEx.ThreadDispatcher.BeginInvoke(DispatcherPriority.Send,
                (DispatcherOperationCallback)delegate(object args)
                {
                    DynamicRendererHostVisual[] hostVisuals = (DynamicRendererHostVisual[])args;
                    VisualTarget vt;
                    // Query the VisualTarget properties to initialize them.
                    vt = hostVisuals[0].VisualTarget;
                    vt = hostVisuals[1].VisualTarget;
                    
                    return null;
                },
                myArgs);
                */
            }
        }

        /// <summary>
        /// Unhoot the visual target.
        /// This method is called from the application Dispatcher
        /// </summary>
        public virtual void DestroyRealTimeVisuals()
        {
            // Only need to handle if already created visuals.
            if (mainContainerVisualEx != null && rawInkHostVisual1Ex != null)
            {
                // Make sure we unhook the rendercomplete event.
                if (waitingForRenderCompleteEx)
                {
                    MediaContext.From(applicationDispatcherEx).RenderComplete -= onRenderCompleteEx;
                    waitingForRenderCompleteEx = false;
                }

                mainContainerVisualEx.Children.Remove(rawInkHostVisual1Ex);
                mainContainerVisualEx.Children.Remove(rawInkHostVisual2Ex);

                renderCompleteStrokeInfoEx = null;

                DynamicRendererThreadManager renderingThread = renderingThreadEx; // keep ref to keep it alive in this routine
                Dispatcher drDispatcher = renderingThread != null ? renderingThread.ThreadDispatcher : null;

                if (drDispatcher != null)
                {
                    drDispatcher.BeginInvoke(DispatcherPriority.Send,
                    (DispatcherOperationCallback)delegate(object unused)
                    {
                        renderCompleteDRThreadStrokeInfoListEx.Clear();
                        
                        drDispatcher = renderingThread.ThreadDispatcher;
                        
                        if (drDispatcher != null && waitingForDRThreadRenderCompleteEx)
                        {
                            MediaContext.From(drDispatcher).RenderComplete -= onDRThreadRenderCompleteEx;
                        }
                        waitingForDRThreadRenderCompleteEx = false;
                
                        return null;
                    },
                    null);
                }

                // Make sure to free up inking thread ref to ensure thread shuts down properly.
                renderingThreadEx = null;

                rawInkHostVisual1Ex = null;
                rawInkHostVisual2Ex = null;
                currentHostVisualEx = null;  // We create new HostVisuals next time we're enabled.

                AbortAllStrokes(); // Doing this here avoids doing a begininvoke to enter the rendering thread (avoid reentrancy).
            }
        }

        /////////////////////////////////////////////////////////////////////
         public Dispatcher          applicationDispatcherEx;
        public Geometry            zeroSizedFrozenRectEx;
        public DrawingAttributes   drawAttrsSourceEx = new DrawingAttributes();
        public List<StrokeInfo>            strokeInfoListEx = new List<StrokeInfo>();

        // Visuals layout:
        // 
        //  mainContainerVisualEx (root of inking tree - RootVisual [on app Dispatcher])
        //     |
        //     +-- _mainRawInkDispatcher (app dispatcher based stylus events renderer here [on app dispatcher])
        //     |
        //     +-- rawInkHostVisual1Ex (HostVisual for inking on separate thread [on app dispatcher])
        //     |          |
        //     |          +-- VisualTarget ([on RealTimeInkingDispatcher thread])
        //     |
        //     +-- rawInkHostVisual2Ex (HostVisual for inking on separate thread [on app dispatcher])
        //                |
        //                +-- VisualTarget ([on RealTimeInkingDispatcher thread])
        // 
        public ContainerVisual              mainContainerVisualEx;
        public ContainerVisual              mainRawInkContainerVisualEx;
        public DynamicRendererHostVisual    rawInkHostVisual1Ex;
        public DynamicRendererHostVisual    rawInkHostVisual2Ex;

        public DynamicRendererHostVisual currentHostVisualEx; // Current HV.

        // For OnRenderComplete support (for UI Thread)
        public EventHandler onRenderCompleteEx;
        public bool waitingForRenderCompleteEx;
        public object siLockEx = new object();
        public StrokeInfo  renderCompleteStrokeInfoEx;

        // On internal real time ink rendering thread.
        internal DynamicRendererThreadManager renderingThreadEx;

        // For OnRenderComplete support (for DynamicRenderer Thread)
        public EventHandler onDRThreadRenderCompleteEx;
        public bool waitingForDRThreadRenderCompleteEx;
        public Queue<StrokeInfo>    renderCompleteDRThreadStrokeInfoListEx = new Queue<StrokeInfo>();
}
}
