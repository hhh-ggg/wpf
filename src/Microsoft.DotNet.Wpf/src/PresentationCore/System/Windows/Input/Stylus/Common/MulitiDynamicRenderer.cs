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

using SR = MS.Internal.PresentationCore.SR;
using SRID = MS.Internal.PresentationCore.SRID;

namespace System.Windows.Input.StylusPlugIns
{
    /////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// [TBS]
    /// </summary>
    public class MulitiDynamicRenderer : DynamicRenderer
    {
        /////////////////////////////////////////////////////////////////////

        /// <summary>
        /// [TBS] - On UIContext
        /// </summary>
        public MulitiDynamicRenderer() : base()
        {
            zeroSizedFrozenRectEx = new RectangleGeometry(new Rect(0, 0, 0, 0));
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
        public override void Reset(StylusDevice stylusDevice, StylusPointCollection stylusPoints)
        {
            //Trace.WriteLine("Reset");
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
            using (applicationDispatcherEx.DisableProcessing())
            {
                lock (siLockEx)
                {
                    AbortAllStrokes(); // stop any current inking strokes

                    // Now create new si and insert it in the list.
                    StrokeInfo si = new StrokeInfo(DrawingAttributes,
                                                   (stylusDevice != null) ? stylusDevice.Id : 0,
                                                   Environment.TickCount, GetCurrentHostVisual());
                    int deviceId = (stylusDevice != null) ? stylusDevice.Id : 0;
                    if (_multiStrokeInfoDic.ContainsKey(deviceId))
                    {
                        _multiStrokeInfoDic[deviceId].Add(si);
                    }
                    else
                    {
                        List<StrokeInfo> strokeInfoList = new List<StrokeInfo>();
                        strokeInfoList.Add(si);
                        _multiStrokeInfoDic.Add(deviceId, strokeInfoList);
                    }
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
        public override Visual RootVisual
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
            //Trace.WriteLine("OnAdded");
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
            //Trace.WriteLine("OnRemoved");
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
            //Trace.WriteLine("OnIsActiveForInputChanged");
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
            //Trace.WriteLine("OnStylusEnter");
            HandleStylusEnterLeave(rawStylusInput, true, confirmed);
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS] - On pen threads or app thread
        /// </summary>
        protected override void OnStylusLeave(RawStylusInput rawStylusInput, bool confirmed)
        {
            //Trace.WriteLine("OnStylusLeave");
            HandleStylusEnterLeave(rawStylusInput, false, confirmed);
        }

        public override void HandleStylusEnterLeave(RawStylusInput rawStylusInput, bool isEnter, bool isConfirmed)
        {
            //Trace.WriteLine("HandleStylusEnterLeave");
            // See if we need to abort a stroke due to entering or leaving within a stroke.
            if (isConfirmed)
            {
                StrokeInfo si = FindStrokeInfo(rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp);

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
            //Trace.WriteLine("OnEnabledChanged");
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
            //Trace.WriteLine("OnStylusDown" + this.iTestByhjc + ":eanbled:" + this.Enabled + "isNewSingle:" + isNewSingle);
            // Only allow inking if someone has queried our RootVisual.
            if (mainContainerVisualEx != null)
            {
                StrokeInfo si;

                lock (siLockEx)
                {
                    si = FindStrokeInfo(rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp);

                    // If we find we are already in the middle of stroke then bail out.
                    // Can only ink with one stylus at a time.

                    {
                        si = new StrokeInfo(DrawingAttributes, rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp, GetCurrentHostVisual());
                        si.allPoints = new StylusPointCollection(rawStylusInput.GetStylusPoints().Description);
                        StylusPointCollection upCollectionPoints = rawStylusInput.GetStylusPoints();
                        if (null != upCollectionPoints && 0 != upCollectionPoints.Count && null != si.allPoints)
                        {
                            //si.allPoints.Add(upCollectionPoints);
                        }

                        if (_multiStrokeInfoDic.ContainsKey(rawStylusInput.StylusDeviceId))
                        {
                            _multiStrokeInfoDic[rawStylusInput.StylusDeviceId].Add(si);
                        }
                        else
                        {
                            List<StrokeInfo> strokeInfoList = new List<StrokeInfo>();
                            strokeInfoList.Add(si);
                            _multiStrokeInfoDic.Add(rawStylusInput.StylusDeviceId, strokeInfoList);
                        }
                    }

                }

                rawStylusInput.NotifyWhenProcessed(si);
                //Trace.WriteLine("down:" + rawStylusInput.StylusDeviceId + "x:" + si.allPoints[0].X + "y:" + si.allPoints[0].Y);
                //if(si.canRender)
                {
                    //RenderPackets(rawStylusInput.GetStylusPoints(), si);
                }

            }
        }

        private bool isValidPoint(StylusPoint p1, StylusPoint p2)
        {
            if (Math.Abs(p1.X - p2.X) >= 50 || Math.Abs(p1.Y - p2.Y) >= 50)
            {
                return false;
            }

            return true;
        }

        private bool isMaxLength(StylusPoint p1, StylusPoint p2)
        {

            double dLength = ((p1.X - p2.X) * (p1.X - p2.X)) + ((p1.Y - p2.Y) * (p1.Y - p2.Y));
            if (dLength >= 100)
            {
                return true;
            }

            return false;
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
                StrokeInfo si = FindStrokeInfo(rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp);

                if (si != null && (si.StylusId == rawStylusInput.StylusDeviceId))
                {
                    //Trace.WriteLine("move:" + rawStylusInput.StylusDeviceId + "x:" + si.allPoints[0].X + "y:" + si.allPoints[0].Y);
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
                            si.allPoints.Add(upCollectionPoints);
                            if (si.allPoints.Count >= 6 && !si.canRender)
                            {
                                si.canRender = true;
                                si.allPoints.Clear();
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

            //Trace.WriteLine("OnStylusUp" + this.iTestByhjc + ":eanbled:" + this.Enabled + "isNewSingle:" + isNewSingle);
            //Trace.WriteLine("OnStylusUp");
            // Only allow inking if someone has queried our RootVisual.
            if (mainContainerVisualEx != null)
            {
                StrokeInfo si = FindStrokeInfo(rawStylusInput.StylusDeviceId, rawStylusInput.Timestamp);

                if (si != null &&
                    ((si.StylusId == rawStylusInput.StylusDeviceId) ||
                     (rawStylusInput.StylusDeviceId == 0 &&
                      (si.IsReset ||
                      (si.IsTimestampAfter(rawStylusInput.Timestamp) && IsStylusUp(si.StylusId))))))
                {
                    si.SeenUp = true;
                    si.LastTime = rawStylusInput.Timestamp;
                    StylusPointCollection upCollectionPoints = rawStylusInput.GetStylusPoints();
                    if (null != upCollectionPoints && 0 != upCollectionPoints.Count && null != si.allPoints)
                    {
                        si.allPoints.Add(upCollectionPoints);
                        if (null != applicationDispatcherEx)
                        {
                            StylusPointCollection strokePoints = new StylusPointCollection(si.allPoints);
                            applicationDispatcherEx.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                            {
                                //removeSiInfo(si);
                                stylusUpProcess(strokePoints);
                                MediaContext.From(applicationDispatcherEx).RenderMessageHandler(null);
                                MediaContext.From(applicationDispatcherEx).CommitChannel();
                                //removeSiInfo(si);
                            }));

                        }
                    }

                    rawStylusInput.NotifyWhenProcessed(si);
                }
            }
        }

        public override bool IsStylusUp(int stylusId)
        {
            //Trace.WriteLine("IsStylusUp");
            TabletDeviceCollection tabletDevices = Tablet.TabletDevices;
            for (int i = 0; i < tabletDevices.Count; i++)
            {
                TabletDevice tabletDevice = tabletDevices[i];
                for (int j = 0; j < tabletDevice.StylusDevices.Count; j++)
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
        public override void OnRenderComplete()
        {
            //Trace.WriteLine("OnRenderComplete");
            StrokeInfo si = renderCompleteStrokeInfoEx;
            Debug.Assert(si != null);  // should never get here unless we are transitioning a stroke.

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

        public override void RemoveDynamicRendererVisualAndNotifyWhenDone(StrokeInfo si)
        {
            //Trace.WriteLine("RemoveDynamicRendererVisualAndNotifyWhenDone");
            if (si != null)
            {
                DynamicRendererThreadManager renderingThread = renderingThreadEx; // Keep it alive
                if (renderingThread != null)
                {
                    // We are being called by the main UI thread, so marshal over to
                    // the inking thread before cleaning up the stroke visual.
                    renderingThread.ThreadDispatcher.BeginInvoke(DispatcherPriority.Send,
                    (DispatcherOperationCallback)delegate (object unused)
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


        public override void NotifyAppOfDRThreadRenderComplete(StrokeInfo si)
        {
            //Trace.WriteLine("NotifyAppOfDRThreadRenderComplete");
            Dispatcher dispatcher = applicationDispatcherEx;
            if (dispatcher != null)
            {
                // We are being called by the inking thread, so marshal over to
                // the UI thread before handling the StrokeInfos that are done rendering.
                dispatcher.BeginInvoke(DispatcherPriority.Send,
                (DispatcherOperationCallback)delegate (object unused)
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


        public override void OnDRThreadRenderComplete(object sender, EventArgs e)
        {
            //Trace.WriteLine("OnDRThreadRenderComplete");
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
                            (DispatcherOperationCallback)delegate (object unused)
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
            //Trace.WriteLine("OnStylusDownProcessed");
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
            //Trace.WriteLine("OnStylusUpProcessed");
            StrokeInfo si = callbackData as StrokeInfo;

            if (si == null)
                return;

            // clean up stroke visuals (and move to transitional VisualTarget as needed)
            TransitionStrokeVisuals(si, !targetVerified);
        }

        public override void OnInternalRenderComplete(object sender, EventArgs e)
        {
            //Trace.WriteLine("OnInternalRenderComplete");
            // First unhook event handler
            MediaContext.From(applicationDispatcherEx).RenderComplete -= onRenderCompleteEx;
            waitingForRenderCompleteEx = false;

            // Make sure lock() doesn't cause reentrancy.
            using (applicationDispatcherEx.DisableProcessing())
            {
                // Now notify event happened.
                OnRenderComplete();
            }
        }


        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        public override void NotifyOnNextRenderComplete()
        {
            //Trace.WriteLine("NotifyOnNextRenderComplete");
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
        protected override void OnDraw(DrawingContext drawingContext,
                                        StylusPointCollection stylusPoints,
                                        Geometry geometry,
                                        Brush fillBrush)
        {
            //Trace.WriteLine("OnDraw");
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
        protected override void OnDrawingAttributesReplaced()
        {
            //Trace.WriteLine("OnDrawingAttributesReplaced");
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// [TBS]
        /// </summary>
        protected virtual void stylusUpProcess(StylusPointCollection stylusPoints)
        {

        }

        void removeSiInfo(StrokeInfo si)
        {
            this.GetDispatcher().BeginInvoke(new Action(() =>
            {
                lock (siLockEx)
                {
                    ((ContainerVisual)si.StrokeHV.VisualTarget.RootVisual).Children.Remove(si.StrokeRTICV);
                }

            }));
        }

        /////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Retrieves the Dispatcher for the thread used for rendering dynamic strokes
        /// when receiving data from the stylus input thread(s).
        /// </summary>
        protected override Dispatcher GetDispatcher()
        {
            //Trace.WriteLine("GetDispatcher");
            return renderingThreadEx != null ? renderingThreadEx.ThreadDispatcher : null;
        }

        /////////////////////////////////////////////////////////////////////

        protected override void RenderPackets(StylusPointCollection stylusPoints, StrokeInfo si)
        {
            //Trace.WriteLine("RenderPackets");
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
                        (DispatcherOperationCallback)delegate (object unused)
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

        protected override void AbortAllStrokes()
        {
            //Trace.WriteLine("AbortAllStrokes");
            lock (siLockEx)
            {
                //获取所有的key
                List<int> strokeInfosKeys = new List<int>();
                foreach (var strokeListItem in _multiStrokeInfoDic)
                {
                    strokeInfosKeys.Add(strokeListItem.Key);

                }

                foreach (var strokeId in strokeInfosKeys)
                {
                    if (_multiStrokeInfoDic[strokeId].Count > 0)
                    {
                        StrokeInfo si = _multiStrokeInfoDic[strokeId][0];
                        if (_multiStrokeInfoDic.ContainsKey(si.StylusId))
                        {
                            _multiStrokeInfoDic.Remove(si.StylusId);
                        }

                        TransitionStrokeVisualsEX(si, true);
                    }
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
        protected override void TransitionStrokeVisuals(StrokeInfo si, bool abortStroke)
        {
            //Trace.WriteLine("TransitionStrokeVisuals");
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

        void TransitionStrokeVisualsEX(StrokeInfo si, bool abortStroke)
        {
            //Trace.WriteLine("TransitionStrokeVisuals");
            // Make sure we don't get any more input for this stroke.


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
        protected override DynamicRendererHostVisual GetCurrentHostVisual()
        {
            //Trace.WriteLine("GetCurrentHostVisual");
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
        protected override void TransitionComplete(StrokeInfo si)
        {
            //Trace.WriteLine("TransitionComplete");
            // make sure lock does not cause reentrancy on application thread!
            using (applicationDispatcherEx.DisableProcessing())
            {
                lock (siLockEx)
                {
                    si.StrokeHV.RemoveStrokeInfoRef(si);
                }
            }
        }

        protected override void RemoveStrokeInfo(StrokeInfo si)
        {
            //Trace.WriteLine("RemoveStrokeInfo");
            lock (siLockEx)
            {
                if (_multiStrokeInfoDic.ContainsKey(si.StylusId))
                {
                    _multiStrokeInfoDic.Remove(si.StylusId);
                }
            }
        }

        private StrokeInfo FindStrokeInfo(int deviceId, int timestamp)
        {
            //Trace.WriteLine("FindStrokeInfo");
            lock (siLockEx)
            {
                if (_multiStrokeInfoDic.ContainsKey(deviceId))
                {
                    List<StrokeInfo> strokeInfoList = _multiStrokeInfoDic[deviceId];
                    for (int i = 0; i < strokeInfoList.Count; i++)
                    {
                        StrokeInfo siCur = strokeInfoList[i];

                        if (siCur.IsTimestampWithin(timestamp))
                        {
                            return siCur;
                        }
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
        public override DrawingAttributes DrawingAttributes
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

        public override void CreateInkingVisuals()
        {
            //Trace.WriteLine("CreateInkingVisuals");
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
        public override void CreateRealTimeVisuals()
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
        public override void DestroyRealTimeVisuals()
        {
            //Trace.WriteLine("DestroyRealTimeVisuals");
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
                    (DispatcherOperationCallback)delegate (object unused)
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
        Dictionary<int, List<StrokeInfo>> _multiStrokeInfoDic = new Dictionary<int, List<StrokeInfo>>();
    }
}
