using HarmonyLib;
using Reptile.Phone;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace BRCCodeDmg.Patches
{
    [HarmonyPatch]
    internal static class LinkCablePhoneNavigationPatch
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var phoneType = typeof(Phone);
            var candidates = new[]
            {
                "PhoneMoveLeft",
                "OnPressLeft",
                "HandlePressedBackButton",
                "CloseCurrentApp",
            };
            foreach (var name in candidates)
            {
                var method = AccessTools.Method(phoneType, name);
                if (method != null)
                    yield return method;
            }
        }

        static bool Prefix()
        {
            if (PhoneMenuInputRouter.TryHandleBack())
            {
                PopupState.SuppressPhoneNavFor(0.2f);
                return false;
            }

            if (PopupState.IsSuppressed)
                return false;

            return true;
        }
    }

    [HarmonyPatch]
    internal static class MasterMenuPhoneDirectionPatch
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new HashSet<string>
            {
                "PhoneMoveUp",
                "PhoneMoveDown",
                "PhoneMoveRight",
                "OnPressUp",
                "OnPressDown",
                "OnPressRight",
                "OnReleaseRight",
                "OnHoldUp",
                "OnHoldDown",
                "OnReleaseUp",
                "OnReleaseDown",
            };

            foreach (var type in GetPhoneTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    if (names.Contains(method.Name))
                        yield return method;
                }
            }
        }

        static bool Prefix(MethodBase __originalMethod)
        {
            if (__originalMethod == null)
                return true;

            switch (__originalMethod.Name)
            {
                case "PhoneMoveUp":
                case "OnPressUp":
                    return !PhoneMenuInputRouter.TryHandleUp(false);

                case "OnHoldUp":
                    return !PhoneMenuInputRouter.TryHandleUp(true);

                case "OnReleaseUp":
                    PhoneMenuInputRouter.ResetUp();
                    return true;

                case "PhoneMoveDown":
                case "OnPressDown":
                    return !PhoneMenuInputRouter.TryHandleDown(false);

                case "OnHoldDown":
                    return !PhoneMenuInputRouter.TryHandleDown(true);

                case "OnReleaseDown":
                    PhoneMenuInputRouter.ResetDown();
                    return true;

                case "PhoneMoveRight":
                case "OnPressRight":
                    return !PhoneMenuInputRouter.TryHandleRight();

                case "OnReleaseRight":
                    AppCodeDmg.MarkRightMenuOpenReleased();
                    return true;
            }

            return true;
        }

        private static IEnumerable<Type> GetPhoneTypes()
        {
            Type[] types;
            try
            {
                types = typeof(Phone).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            if (types == null) yield break;
            foreach (var type in types)
            {
                if (type == null || type.Namespace == null) continue;
                if (type.Namespace == "Reptile.Phone" || type.Namespace.StartsWith("Reptile.Phone."))
                    yield return type;
            }
        }
    }

    internal static class PhoneMenuInputRouter
    {
        private const float PressCooldown = 0.11f;
        private const float RepeatDelay = 0.34f;
        private const float RepeatInterval = 0.075f;
        private const float HoldResetGap = 0.25f;
        private const float ButtonCooldown = 0.16f;
        private static float _lastUpTime = -1f;
        private static float _lastDownTime = -1f;
        private static float _upPressTime = -1f;
        private static float _downPressTime = -1f;
        private static float _lastUpSignalTime = -1f;
        private static float _lastDownSignalTime = -1f;
        private static int _lastUpFrame = -1;
        private static int _lastDownFrame = -1;
        private static float _lastRightTime = -1f;
        private static float _lastBackTime = -1f;

        public static bool TryHandleUp(bool held)
        {
            return TryHandleDirection(ref _lastUpTime, ref _upPressTime, ref _lastUpSignalTime, ref _lastUpFrame, held, () =>
                GBPaletteMenu.TryHandlePhoneUp()
                || VolumeMenu.TryHandlePhoneUp()
                || RomSelectMenu.TryHandlePhoneUp()
                || LinkCableConnectDialog.TryHandlePhoneUp()
                || LinkCablePlayerList.TryHandlePhoneUp()
                || MasterMenu.TryHandlePhoneUp());
        }

        public static bool TryHandleDown(bool held)
        {
            return TryHandleDirection(ref _lastDownTime, ref _downPressTime, ref _lastDownSignalTime, ref _lastDownFrame, held, () =>
                GBPaletteMenu.TryHandlePhoneDown()
                || VolumeMenu.TryHandlePhoneDown()
                || RomSelectMenu.TryHandlePhoneDown()
                || LinkCableConnectDialog.TryHandlePhoneDown()
                || LinkCablePlayerList.TryHandlePhoneDown()
                || MasterMenu.TryHandlePhoneDown());
        }

        public static void ResetUp()
        {
            _upPressTime = -1f;
            _lastUpSignalTime = -1f;
        }

        public static void ResetDown()
        {
            _downPressTime = -1f;
            _lastDownSignalTime = -1f;
        }

        public static bool ConsumedUpThisFrame()
        {
            return _lastUpFrame == Time.frameCount;
        }

        public static bool ConsumedDownThisFrame()
        {
            return _lastDownFrame == Time.frameCount;
        }

        public static bool TryHandleRight()
        {
            if (!AnyMenuVisible())
            {
                if (PopupState.ShouldSuppressMenuInputForChat())
                    return false;

                if (AppCodeDmg.ShouldBlockRightMenuOpenSignal())
                    return true;

                float now = Time.unscaledTime;
                if (now - _lastRightTime < ButtonCooldown)
                    return true;

                bool opened = AppCodeDmg.TryOpenMasterMenuFromRightInput();
                if (opened)
                    _lastRightTime = now;

                return opened;
            }

            // After a sub-menu handles Right (confirm/select), arm the release guard so
            // the same Right press doesn't immediately reopen MasterMenu once menus close.
            bool subHandled = TryHandle(ref _lastRightTime, ButtonCooldown, () =>
                GBPaletteMenu.TryHandlePhoneRight()
                || VolumeMenu.TryHandlePhoneRight()
                || RomSelectMenu.TryHandlePhoneRight()
                || LinkCableConnectDialog.TryHandlePhoneRight()
                || LinkCablePlayerList.TryHandlePhoneRight()
                || MasterMenu.TryHandlePhoneRight());
            if (subHandled && !AnyMenuVisible())
                AppCodeDmg.BeginRightMenuOpenReleaseGuard();
            return subHandled;
        }

        public static bool TryHandleBack()
        {
            return TryHandle(ref _lastBackTime, ButtonCooldown, () =>
                GBPaletteMenu.TryHandlePhoneBack()
                || VolumeMenu.TryHandlePhoneBack()
                || RomSelectMenu.TryHandlePhoneBack()
                || LinkCableConnectDialog.TryHandlePhoneBack()
                || LinkCablePlayerList.TryHandlePhoneBack()
                || MasterMenu.TryHandlePhoneBack());
        }

        private static bool TryHandleDirection(ref float lastTime, ref float pressTime, ref float lastSignalTime, ref int lastFrame, bool held, Func<bool> handler)
        {
            if (!AnyMenuVisible())
                return false;

            if (PopupState.ShouldSuppressMenuInputForChat())
                return false;

            float now = Time.unscaledTime;
            lastFrame = Time.frameCount;

            if (held)
            {
                if (pressTime < 0f || now - lastSignalTime > HoldResetGap)
                    pressTime = now;

                lastSignalTime = now;

                if (now - pressTime < RepeatDelay)
                    return true;

                if (now - lastTime < RepeatInterval)
                    return true;
            }
            else
            {
                lastSignalTime = now;
                if (now - lastTime < PressCooldown)
                    return true;

                pressTime = now;
            }

            bool handled = handler();
            if (handled)
                lastTime = now;

            return handled;
        }

        private static bool TryHandle(ref float lastTime, float cooldown, Func<bool> handler)
        {
            if (!AnyMenuVisible())
                return false;

            if (PopupState.ShouldSuppressMenuInputForChat())
                return false;

            float now = Time.unscaledTime;
            if (now - lastTime < cooldown)
                return true;

            bool handled = handler();
            if (handled)
                lastTime = now;

            return handled;
        }

        private static bool AnyMenuVisible()
        {
            return GBPaletteMenu.IsVisible
                || VolumeMenu.IsVisible
                || RomSelectMenu.IsVisible
                || LinkCableConnectDialog.IsVisible
                || LinkCablePlayerList.IsVisible
                || MasterMenu.IsVisible;
        }
    }
}