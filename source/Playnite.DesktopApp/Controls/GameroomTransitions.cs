using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Fades and slides content in when it changes: <c>&lt;ContentControl GameroomTransitions.AnimateContent="True" /&gt;</c>.
    /// </summary>
    public static class GameroomTransitions
    {
        private static readonly DependencyPropertyDescriptor contentDescriptor =
            DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));

        public static readonly DependencyProperty AnimateContentProperty = DependencyProperty.RegisterAttached(
            "AnimateContent", typeof(bool), typeof(GameroomTransitions), new PropertyMetadata(false, OnAnimateContentChanged));

        public static bool GetAnimateContent(DependencyObject obj)
        {
            return (bool)obj.GetValue(AnimateContentProperty);
        }

        public static void SetAnimateContent(DependencyObject obj, bool value)
        {
            obj.SetValue(AnimateContentProperty, value);
        }

        private static void OnAnimateContentChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
        {
            if (!(obj is ContentControl control))
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                contentDescriptor.AddValueChanged(control, Content_Changed);
            }
            else
            {
                contentDescriptor.RemoveValueChanged(control, Content_Changed);
            }
        }

        private static void Content_Changed(object sender, EventArgs e)
        {
            var control = (ContentControl)sender;
            if (!(control.RenderTransform is TranslateTransform offset) || offset.IsFrozen)
            {
                offset = new TranslateTransform();
                control.RenderTransform = offset;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // Starts partly visible: fading from nothing showed the empty window for a moment, like a blink.
            control.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        }
    }
}
