#!/usr/bin/env python3
"""Spike: set one LibraryOptions field on a library.  set_lib_option.py <server> <library> <Field> <json value>"""
import json
import sys

import jf

srv, lib, field, value = sys.argv[1], sys.argv[2], sys.argv[3], json.loads(sys.argv[4])
folder = [f for f in jf.call(srv, "GET", "/Library/VirtualFolders") if f["Name"] == lib][0]
opts = folder["LibraryOptions"]
opts[field] = value
jf.call(srv, "POST", "/Library/VirtualFolders/LibraryOptions", {"Id": folder["ItemId"], "LibraryOptions": opts})
print(lib, field, "=", [f for f in jf.call(srv, "GET", "/Library/VirtualFolders") if f["Name"] == lib][0]["LibraryOptions"][field])
