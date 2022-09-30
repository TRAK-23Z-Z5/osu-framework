// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Logging;
using osu.Framework.Platform.SDL2;
using osuTK;
using SDL2;

namespace osu.Framework.Platform
{
    public partial class SDL2DesktopWindow
    {
        private bool focused;

        /// <summary>
        /// Whether the window currently has focus.
        /// </summary>
        public bool Focused
        {
            get => focused;
            private set
            {
                if (value == focused)
                    return;

                isActive.Value = focused = value;
            }
        }

        public WindowMode DefaultWindowMode => Configuration.WindowMode.Windowed;

        /// <summary>
        /// Returns the window modes that the platform should support by default.
        /// </summary>
        protected virtual IEnumerable<WindowMode> DefaultSupportedWindowModes => Enum.GetValues(typeof(WindowMode)).OfType<WindowMode>();

        private Point position;

        /// <summary>
        /// Returns or sets the window's position in screen space. Only valid when in <see cref="osu.Framework.Configuration.WindowMode.Windowed"/>
        /// </summary>
        public Point Position
        {
            get => position;
            set
            {
                position = value;
                ScheduleCommand(() => SDL.SDL_SetWindowPosition(SDLWindowHandle, value.X, value.Y));
            }
        }

        private bool resizable = true;

        /// <summary>
        /// Returns or sets whether the window is resizable or not. Only valid when in <see cref="osu.Framework.Platform.WindowState.Normal"/>.
        /// </summary>
        public bool Resizable
        {
            get => resizable;
            set
            {
                if (resizable == value)
                    return;

                resizable = value;
                ScheduleCommand(() => SDL.SDL_SetWindowResizable(SDLWindowHandle, value ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE));
            }
        }

        private Size size = new Size(default_width, default_height);

        /// <summary>
        /// Returns or sets the window's internal size, before scaling.
        /// </summary>
        public virtual Size Size
        {
            get => size;
            protected set
            {
                if (value.Equals(size)) return;

                size = value;
                Resized?.Invoke();
            }
        }

        public Size MinSize
        {
            get => sizeWindowed.MinValue;
            set => sizeWindowed.MinValue = value;
        }

        public Size MaxSize
        {
            get => sizeWindowed.MaxValue;
            set => sizeWindowed.MaxValue = value;
        }

        public Bindable<Display> CurrentDisplayBindable { get; } = new Bindable<Display>();

        public Bindable<WindowMode> WindowMode { get; } = new Bindable<WindowMode>();

        private readonly BindableBool isActive = new BindableBool();

        public IBindable<bool> IsActive => isActive;

        private readonly BindableBool cursorInWindow = new BindableBool();

        public IBindable<bool> CursorInWindow => cursorInWindow;

        public IBindableList<WindowMode> SupportedWindowModes { get; }

        private bool visible;

        /// <summary>
        /// Enables or disables the window visibility.
        /// </summary>
        public bool Visible
        {
            get => visible;
            set
            {
                visible = value;
                ScheduleCommand(() =>
                {
                    if (value)
                        SDL.SDL_ShowWindow(SDLWindowHandle);
                    else
                        SDL.SDL_HideWindow(SDLWindowHandle);
                });
            }
        }

        private WindowState windowState = WindowState.Normal;

        private WindowState? pendingWindowState;

        /// <summary>
        /// Returns or sets the window's current <see cref="WindowState"/>.
        /// </summary>
        public WindowState WindowState
        {
            get => windowState;
            set
            {
                if (pendingWindowState == null && windowState == value)
                    return;

                pendingWindowState = value;
            }
        }

        /// <summary>
        /// Stores whether the window used to be in maximised state or not.
        /// Used to properly decide what window state to pick when switching to windowed mode (see <see cref="WindowMode"/> change event)
        /// </summary>
        private bool windowMaximised;

        /// <summary>
        /// Returns the drawable area, after scaling.
        /// </summary>
        public Size ClientSize => new Size(Size.Width, Size.Height);

        public float Scale = 1;

        /// <summary>
        /// Queries the physical displays and their supported resolutions.
        /// </summary>
        public IEnumerable<Display> Displays => Enumerable.Range(0, SDL.SDL_GetNumVideoDisplays()).Select(displayFromSDL);

        /// <summary>
        /// Gets the <see cref="Display"/> that has been set as "primary" or "default" in the operating system.
        /// </summary>
        public virtual Display PrimaryDisplay => Displays.First();

        private Display currentDisplay = null!;
        private int displayIndex = -1;

        /// <summary>
        /// Gets or sets the <see cref="Display"/> that this window is currently on.
        /// </summary>
        public Display CurrentDisplay { get; private set; } = null!;

        private readonly Bindable<DisplayMode> currentDisplayMode = new Bindable<DisplayMode>();

        /// <summary>
        /// The <see cref="DisplayMode"/> for the display that this window is currently on.
        /// </summary>
        public IBindable<DisplayMode> CurrentDisplayMode => currentDisplayMode;

        private Rectangle windowDisplayBounds
        {
            get
            {
                SDL.SDL_GetDisplayBounds(displayIndex, out var rect);
                return new Rectangle(rect.x, rect.y, rect.w, rect.h);
            }
        }

        private readonly BindableSize sizeFullscreen = new BindableSize();
        private readonly BindableSize sizeWindowed = new BindableSize();
        private readonly BindableDouble windowPositionX = new BindableDouble();
        private readonly BindableDouble windowPositionY = new BindableDouble();
        private readonly Bindable<DisplayIndex> windowDisplayIndexBindable = new Bindable<DisplayIndex>();

        /// <summary>
        /// Updates the client size and the scale according to the window.
        /// </summary>
        /// <returns>Whether the window size has been changed after updating.</returns>
        private void updateWindowSize()
        {
            SDL.SDL_GL_GetDrawableSize(SDLWindowHandle, out int w, out int h);
            SDL.SDL_GetWindowSize(SDLWindowHandle, out int actualW, out int _);

            // When minimised on windows, values may be zero.
            // If we receive zeroes for either of these, it seems safe to completely ignore them.
            if (actualW <= 0 || w <= 0)
                return;

            Scale = (float)w / actualW;
            Size = new Size(w, h);

            // This function may be invoked before the SDL internal states are all changed. (as documented here: https://wiki.libsdl.org/SDL_SetEventFilter)
            // Scheduling the store to config until after the event poll has run will ensure the window is in the correct state.
            EventScheduler.AddOnce(storeWindowSizeToConfig);
        }

        #region SDL Event Handling

        private void handleWindowEvent(SDL.SDL_WindowEvent evtWindow)
        {
            updateWindowSpecifics();

            switch (evtWindow.windowEvent)
            {
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MOVED:
                    // explicitly requery as there are occasions where what SDL has provided us with is not up-to-date.
                    SDL.SDL_GetWindowPosition(SDLWindowHandle, out int x, out int y);
                    var newPosition = new Point(x, y);

                    if (!newPosition.Equals(Position))
                    {
                        position = newPosition;
                        Moved?.Invoke(newPosition);

                        if (WindowMode.Value == Configuration.WindowMode.Windowed)
                            storeWindowPositionToConfig();
                    }

                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED:
                    updateWindowSize();
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_ENTER:
                    cursorInWindow.Value = true;
                    MouseEntered?.Invoke();
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_LEAVE:
                    cursorInWindow.Value = false;
                    MouseLeft?.Invoke();
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_RESTORED:
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
                    Focused = true;
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_MINIMIZED:
                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
                    Focused = false;
                    break;

                case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE:
                    break;
            }
        }

        /// <summary>
        /// Should be run on a regular basis to check for external window state changes.
        /// </summary>
        private void updateWindowSpecifics()
        {
            // don't attempt to run before the window is initialised, as Create() will do so anyway.
            if (SDLWindowHandle == IntPtr.Zero)
                return;

            var stateBefore = windowState;

            // check for a pending user state change and give precedence.
            if (pendingWindowState != null)
            {
                windowState = pendingWindowState.Value;
                pendingWindowState = null;

                updateWindowStateAndSize();
            }
            else
            {
                windowState = ((SDL.SDL_WindowFlags)SDL.SDL_GetWindowFlags(SDLWindowHandle)).ToWindowState();
            }

            if (windowState != stateBefore)
            {
                WindowStateChanged?.Invoke(windowState);
                updateMaximisedState();
            }

            int newDisplayIndex = SDL.SDL_GetWindowDisplayIndex(SDLWindowHandle);

            if (displayIndex != newDisplayIndex)
            {
                displayIndex = newDisplayIndex;
                currentDisplay = Displays.ElementAtOrDefault(displayIndex) ?? PrimaryDisplay;
                CurrentDisplayBindable.Value = currentDisplay;
            }
        }

        /// <summary>
        /// Should be run after a local window state change, to propagate the correct SDL actions.
        /// </summary>
        private void updateWindowStateAndSize()
        {
            // this reset is required even on changing from one fullscreen resolution to another.
            // if it is not included, the GL context will not get the correct size.
            // this is mentioned by multiple sources as an SDL issue, which seems to resolve by similar means (see https://discourse.libsdl.org/t/sdl-setwindowsize-does-not-work-in-fullscreen/20711/4).
            SDL.SDL_SetWindowBordered(SDLWindowHandle, SDL.SDL_bool.SDL_TRUE);
            SDL.SDL_SetWindowFullscreen(SDLWindowHandle, (uint)SDL.SDL_bool.SDL_FALSE);

            switch (windowState)
            {
                case WindowState.Normal:
                    Size = (sizeWindowed.Value * Scale).ToSize();

                    SDL.SDL_RestoreWindow(SDLWindowHandle);
                    SDL.SDL_SetWindowSize(SDLWindowHandle, sizeWindowed.Value.Width, sizeWindowed.Value.Height);
                    SDL.SDL_SetWindowResizable(SDLWindowHandle, Resizable ? SDL.SDL_bool.SDL_TRUE : SDL.SDL_bool.SDL_FALSE);

                    readWindowPositionFromConfig();
                    break;

                case WindowState.Fullscreen:
                    var closestMode = getClosestDisplayMode(sizeFullscreen.Value, currentDisplayMode.Value.RefreshRate, currentDisplay.Index);

                    Size = new Size(closestMode.w, closestMode.h);

                    SDL.SDL_SetWindowDisplayMode(SDLWindowHandle, ref closestMode);
                    SDL.SDL_SetWindowFullscreen(SDLWindowHandle, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN);
                    break;

                case WindowState.FullscreenBorderless:
                    Size = SetBorderless();
                    break;

                case WindowState.Maximised:
                    SDL.SDL_RestoreWindow(SDLWindowHandle);
                    SDL.SDL_MaximizeWindow(SDLWindowHandle);

                    SDL.SDL_GL_GetDrawableSize(SDLWindowHandle, out int w, out int h);
                    Size = new Size(w, h);
                    break;

                case WindowState.Minimised:
                    SDL.SDL_MinimizeWindow(SDLWindowHandle);
                    break;
            }

            updateMaximisedState();

            switch (windowState)
            {
                case WindowState.Fullscreen:
                    if (!updateDisplayMode(true))
                        updateDisplayMode(false);
                    break;

                default:
                    if (!updateDisplayMode(false))
                        updateDisplayMode(true);
                    break;
            }

            bool updateDisplayMode(bool queryFullscreenMode)
            {
                // TODO: displayIndex should be valid here at all times.
                // on startup, the displayIndex will be invalid (-1) due to it being set later in the startup sequence.
                // related to order of operations in `updateWindowSpecifics()`.
                int localIndex = SDL.SDL_GetWindowDisplayIndex(SDLWindowHandle);

                if (localIndex != displayIndex)
                    Logger.Log($"Stored display index ({displayIndex}) doesn't match current index ({localIndex})");

                if (queryFullscreenMode)
                {
                    if (SDL.SDL_GetWindowDisplayMode(SDLWindowHandle, out var mode) >= 0)
                    {
                        currentDisplayMode.Value = mode.ToDisplayMode(localIndex);
                        Logger.Log($"Updated display mode to fullscreen resolution: {mode.w}x{mode.h}@{mode.refresh_rate}, {currentDisplayMode.Value.Format}");
                        return true;
                    }

                    Logger.Log($"Failed to get fullscreen display mode. Display index: {localIndex}. SDL error: {SDL.SDL_GetError()}", level: LogLevel.Error);
                    return false;
                }
                else
                {
                    if (SDL.SDL_GetCurrentDisplayMode(localIndex, out var mode) >= 0)
                    {
                        currentDisplayMode.Value = mode.ToDisplayMode(localIndex);
                        Logger.Log($"Updated display mode to desktop resolution: {mode.w}x{mode.h}@{mode.refresh_rate}, {currentDisplayMode.Value.Format}");
                        return true;
                    }

                    Logger.Log($"Failed to get desktop display mode. Display index: {localIndex}. SDL error: {SDL.SDL_GetError()}", level: LogLevel.Error);
                    return false;
                }
            }
        }

        private void updateMaximisedState()
        {
            if (windowState == WindowState.Normal || windowState == WindowState.Maximised)
                windowMaximised = windowState == WindowState.Maximised;
        }

        private void readWindowPositionFromConfig()
        {
            if (WindowState != WindowState.Normal)
                return;

            var configPosition = new Vector2((float)windowPositionX.Value, (float)windowPositionY.Value);

            var displayBounds = CurrentDisplay.Bounds;
            var windowSize = sizeWindowed.Value;
            int windowX = (int)Math.Round((displayBounds.Width - windowSize.Width) * configPosition.X);
            int windowY = (int)Math.Round((displayBounds.Height - windowSize.Height) * configPosition.Y);

            Position = new Point(windowX + displayBounds.X, windowY + displayBounds.Y);
        }

        private void storeWindowPositionToConfig()
        {
            if (WindowState != WindowState.Normal)
                return;

            var displayBounds = CurrentDisplay.Bounds;

            int windowX = Position.X - displayBounds.X;
            int windowY = Position.Y - displayBounds.Y;

            var windowSize = sizeWindowed.Value;

            windowPositionX.Value = displayBounds.Width > windowSize.Width ? (float)windowX / (displayBounds.Width - windowSize.Width) : 0;
            windowPositionY.Value = displayBounds.Height > windowSize.Height ? (float)windowY / (displayBounds.Height - windowSize.Height) : 0;
        }

        /// <summary>
        /// Set to <c>true</c> while the window size is being stored to config to avoid bindable feedback.
        /// </summary>
        private bool storingSizeToConfig;

        private void storeWindowSizeToConfig()
        {
            if (WindowState != WindowState.Normal)
                return;

            storingSizeToConfig = true;
            sizeWindowed.Value = (Size / Scale).ToSize();
            storingSizeToConfig = false;
        }

        /// <summary>
        /// Prepare display of a borderless window.
        /// </summary>
        /// <returns>
        /// The size of the borderless window's draw area.
        /// </returns>
        protected virtual Size SetBorderless()
        {
            // this is a generally sane method of handling borderless, and works well on macOS and linux.
            SDL.SDL_SetWindowFullscreen(SDLWindowHandle, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);

            return currentDisplay.Bounds.Size;
        }

        #endregion

        public void CycleMode()
        {
            var currentValue = WindowMode.Value;

            do
            {
                switch (currentValue)
                {
                    case Configuration.WindowMode.Windowed:
                        currentValue = Configuration.WindowMode.Borderless;
                        break;

                    case Configuration.WindowMode.Borderless:
                        currentValue = Configuration.WindowMode.Fullscreen;
                        break;

                    case Configuration.WindowMode.Fullscreen:
                        currentValue = Configuration.WindowMode.Windowed;
                        break;
                }
            } while (!SupportedWindowModes.Contains(currentValue) && currentValue != WindowMode.Value);

            WindowMode.Value = currentValue;
        }

        #region Helper functions

        private SDL.SDL_DisplayMode getClosestDisplayMode(Size size, int refreshRate, int displayIndex)
        {
            var targetMode = new SDL.SDL_DisplayMode { w = size.Width, h = size.Height, refresh_rate = refreshRate };

            if (SDL.SDL_GetClosestDisplayMode(displayIndex, ref targetMode, out var mode) != IntPtr.Zero)
                return mode;

            // fallback to current display's native bounds
            targetMode.w = currentDisplay.Bounds.Width;
            targetMode.h = currentDisplay.Bounds.Height;
            targetMode.refresh_rate = 0;

            if (SDL.SDL_GetClosestDisplayMode(displayIndex, ref targetMode, out mode) != IntPtr.Zero)
                return mode;

            // finally return the current mode if everything else fails.
            // not sure this is required.
            if (SDL.SDL_GetWindowDisplayMode(SDLWindowHandle, out mode) >= 0)
                return mode;

            throw new InvalidOperationException("couldn't retrieve valid display mode");
        }

        private static Display displayFromSDL(int displayIndex)
        {
            var displayModes = Enumerable.Range(0, SDL.SDL_GetNumDisplayModes(displayIndex))
                                         .Select(modeIndex =>
                                         {
                                             SDL.SDL_GetDisplayMode(displayIndex, modeIndex, out var mode);
                                             return mode.ToDisplayMode(displayIndex);
                                         })
                                         .ToArray();

            SDL.SDL_GetDisplayBounds(displayIndex, out var rect);
            return new Display(displayIndex, SDL.SDL_GetDisplayName(displayIndex), new Rectangle(rect.x, rect.y, rect.w, rect.h), displayModes);
        }

        #endregion

        #region Events

        /// <summary>
        /// Invoked after the window has resized.
        /// </summary>
        public event Action? Resized;

        /// <summary>
        /// Invoked after the window's state has changed.
        /// </summary>
        public event Action<WindowState>? WindowStateChanged;

        /// <summary>
        /// Invoked when the window moves.
        /// </summary>
        public event Action<Point>? Moved;

        #endregion
    }
}
