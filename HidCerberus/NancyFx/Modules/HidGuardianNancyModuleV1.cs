﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nancy;
using Nancy.Extensions;

namespace HidCerberus.Srv.NancyFx.Modules
{
    public class HidGuardianNancyModuleV1 : NancyModule
    {
        private static readonly IEnumerable<object> ResponseOk = new[] {"OK"};
        private static readonly string[] HardwareIdSplitters = {"\r\n", "\n"};

        private static readonly Regex HardwareIdRegex =
            new Regex(
                @"HID\\[{(]?[0-9A-Fa-z]{8}[-]?([0-9A-Fa-z]{4}[-]?){3}[0-9A-Fa-z]{12}[)}]?|HID\\VID_[a-zA-Z0-9]{4}&PID_[a-zA-Z0-9]{4}");

        public HidGuardianNancyModuleV1() : base("/api/v1")
        {
            #region Whitelist

            Get["/hidguardian/whitelist/add/{id:int}"] = parameters =>
            {
                var id = int.Parse(parameters.id);

                Registry.LocalMachine.CreateSubKey($"{HidWhitelistRegistryKeyBase}\\{id}");

                return Response.AsJson(ResponseOk);
            };

            Get["/hidguardian/whitelist/remove/{id:int}"] = parameters =>
            {
                var id = int.Parse(parameters.id);

                Registry.LocalMachine.DeleteSubKey($"{HidWhitelistRegistryKeyBase}\\{id}");

                return Response.AsJson(ResponseOk);
            };

            Get["/hidguardian/whitelist/get"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidWhitelistRegistryKeyBase);
                var list = wlKey?.GetSubKeyNames();
                wlKey?.Close();

                return Response.AsJson(list);
            };

            Get["/hidguardian/whitelist/purge"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidWhitelistRegistryKeyBase);

                foreach (var subKeyName in wlKey.GetSubKeyNames())
                    Registry.LocalMachine.DeleteSubKey($"{HidWhitelistRegistryKeyBase}\\{subKeyName}");

                return Response.AsJson(ResponseOk);
            };

            #endregion

            #region Force

            Get["/hidguardian/force/get"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase);
                var force = wlKey?.GetValue("Force");
                wlKey?.Close();

                return Response.AsJson(force);
            };

            Get["/hidguardian/force/enable"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase, true);
                wlKey?.SetValue("Force", 1);
                wlKey?.Close();

                return Response.AsJson(ResponseOk);
            };

            Get["/hidguardian/force/disable"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase, true);
                wlKey?.SetValue("Force", 0);
                wlKey?.Close();

                return Response.AsJson(ResponseOk);
            };

            #endregion

            #region Affected

            Get["/hidguardian/affected/get"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase);
                var affected = wlKey?.GetValue("AffectedDevices") as string[];
                wlKey?.Close();

                return Response.AsJson(affected);
            };

            Post["/hidguardian/affected/add"] = parameters =>
            {
                var hwids = Uri.UnescapeDataString(Request.Body.AsString().Split('=')[1]);

                // get existing Hardware IDs
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase, true);
                var affected = (wlKey?.GetValue("AffectedDevices") as string[])?.ToList();

                // split input array
                var idList = hwids.Split(HardwareIdSplitters, StringSplitOptions.None).ToList();

                // kick empty lines
                idList.RemoveAll(string.IsNullOrEmpty);

                if (idList.Any(i => !HardwareIdRegex.IsMatch(i)))
                    return Response.AsJson(new[] {"ERROR", "One or more supplied Hardware IDs are malformed"});

                // fuse arrays
                if (affected != null)
                    idList.AddRange(affected);

                // write back to registry
                wlKey?.SetValue("AffectedDevices", idList.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray(),
                    RegistryValueKind.MultiString);

                wlKey?.Close();

                return Response.AsJson(ResponseOk);
            };

            Post["/hidguardian/affected/remove"] = parameters =>
            {
                var hwids = Uri.UnescapeDataString(Request.Body.AsString().Split('=')[1]);

                // get existing Hardware IDs
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase, true);
                var affected = (wlKey?.GetValue("AffectedDevices") as string[])?.ToList();

                // split input array
                var idList = hwids.Split(HardwareIdSplitters, StringSplitOptions.None).ToList();

                // kick empty lines
                idList.RemoveAll(string.IsNullOrEmpty);

                if (idList.Any(i => !HardwareIdRegex.IsMatch(i)))
                    return Response.AsJson(new[] {"ERROR", "One or more supplied Hardware IDs are malformed"});

                // remove provided values
                affected?.RemoveAll(x => idList.Contains(x));

                // write back to registry
                wlKey?.SetValue("AffectedDevices", affected.ToArray(),
                    RegistryValueKind.MultiString);

                wlKey?.Close();

                return Response.AsJson(ResponseOk);
            };

            Get["/hidguardian/affected/purge"] = _ =>
            {
                var wlKey = Registry.LocalMachine.OpenSubKey(HidGuardianRegistryKeyBase, true);
                wlKey?.SetValue("AffectedDevices", new string[] {}, RegistryValueKind.MultiString);
                wlKey?.Close();

                return Response.AsJson(ResponseOk);
            };

            #endregion
        }

        private static string HidGuardianRegistryKeyBase => @"SYSTEM\CurrentControlSet\Services\HidGuardian\Parameters";

        private static string HidWhitelistRegistryKeyBase
            => $"{HidGuardianRegistryKeyBase}\\Whitelist";
    }
}