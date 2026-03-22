using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundCalcs.UI
{
    public class WindowResizer
    {
        private readonly Window _window;
        private ResizeDirection _resizeDirection;
        private Point _startPoint;
        private bool _isResizing;

        public  const double MIN_WIDTH  = 700;
        public  const double MIN_HEIGHT = 500;

        private double EffectiveMinWidth  => _window.MinWidth  > 0 ? _window.MinWidth  : MIN_WIDTH;
        private double EffectiveMinHeight => _window.MinHeight > 0 ? _window.MinHeight : MIN_HEIGHT;

        public WindowResizer(Window window)
        {
            _window = window;
        }

        public void StartResizing(MouseButtonEventArgs e, ResizeDirection direction)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _resizeDirection = direction;
                _startPoint = e.GetPosition(null);
                _isResizing = true;
                _window.CaptureMouse();

                switch (_resizeDirection)
                {
                    case ResizeDirection.Left:
                    case ResizeDirection.Right:
                        Mouse.OverrideCursor = Cursors.SizeWE;
                        break;
                    case ResizeDirection.Bottom:
                        Mouse.OverrideCursor = Cursors.SizeNS;
                        break;
                    case ResizeDirection.BottomLeft:
                        Mouse.OverrideCursor = Cursors.SizeNESW;
                        break;
                    case ResizeDirection.BottomRight:
                        Mouse.OverrideCursor = Cursors.SizeNWSE;
                        break;
                }
            }
        }

        public void ResizeWindow(MouseEventArgs e)
        {
            if (!_isResizing) return;

            Point currentPoint = e.GetPosition(null);
            double deltaX = currentPoint.X - _startPoint.X;
            double deltaY = currentPoint.Y - _startPoint.Y;

            switch (_resizeDirection)
            {
                case ResizeDirection.Left:
                    ResizeLeft(deltaX);
                    break;
                case ResizeDirection.Right:
                    ResizeRight(deltaX);
                    break;
                case ResizeDirection.Bottom:
                    ResizeBottom(deltaY);
                    break;
                case ResizeDirection.BottomLeft:
                    ResizeLeft(deltaX);
                    ResizeBottom(deltaY);
                    break;
                case ResizeDirection.BottomRight:
                    ResizeRight(deltaX);
                    ResizeBottom(deltaY);
                    break;
            }

            _startPoint = currentPoint;
        }

        public void StopResizing()
        {
            if (_isResizing)
            {
                _isResizing = false;
                _window.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            }
        }

        private void ResizeLeft(double deltaX)
        {
            double newWidth = _window.Width - deltaX;
            if (newWidth >= EffectiveMinWidth)
            {
                _window.Width = newWidth;
                _window.Left += deltaX;
            }
            else
            {
                double offset = _window.Width - EffectiveMinWidth;
                _window.Width = EffectiveMinWidth;
                _window.Left += offset;
            }
        }

        private void ResizeRight(double deltaX)
        {
            double newWidth = _window.Width + deltaX;
            if (newWidth >= EffectiveMinWidth)
            {
                _window.Width = newWidth;
            }
        }

        private void ResizeBottom(double deltaY)
        {
            double newHeight = _window.Height + deltaY;
            if (newHeight >= EffectiveMinHeight)
            {
                _window.Height = newHeight;
            }
        }
    }

    public enum ResizeDirection
    {
        Left,
        Right,
        Bottom,
        BottomLeft,
        BottomRight
    }
}
