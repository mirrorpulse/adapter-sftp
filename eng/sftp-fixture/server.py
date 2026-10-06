"""Loopback SSH/SFTP fixture for process-level Adapter tests."""

import base64
import errno
import hashlib
import json
import os
import posixpath
import socket
import sys
import threading
import time

import paramiko


class Authentication(paramiko.ServerInterface):
    def __init__(self, label):
        self.transport = None
        self.label = label

    def check_auth_password(self, username, password):
        if username == "user-" + self.label and password == "secret-" + self.label:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password"

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED


class FileHandle(paramiko.SFTPHandle):
    def __init__(self, flags, path, root):
        super().__init__(flags)
        self.path = path
        self.root = root

    def stat(self):
        return paramiko.SFTPAttributes.from_stat(os.fstat(self.readfile.fileno()))

    def close(self):
        super().close()
        injected = os.path.join(self.root, ".fixture-edit-after-stage.json")
        if posixpath.basename(self.path).startswith(".mp-stage-") and os.path.exists(injected):
            with open(injected, encoding="utf-8") as source:
                edit = json.load(source)
            os.unlink(injected)
            target = os.path.join(self.root, edit["path"])
            before = os.stat(target)
            with open(target, "wb") as output:
                output.write(edit["content"].encode("utf-8"))
            os.utime(target, ns=(before.st_atime_ns, before.st_mtime_ns))


class Storage(paramiko.SFTPServerInterface):
    def __init__(self, server, *args, **kwargs):
        super().__init__(server)
        self.server = server
        self.root = kwargs["root"]

    def _local(self, path):
        normalized = posixpath.normpath("/" + path).lstrip("/")
        if os.path.exists(os.path.join(self.root, ".fixture-canonical-parent")):
            if normalized == "virtual-root":
                normalized = ""
            elif normalized.startswith("virtual-root/"):
                normalized = normalized[len("virtual-root/"):]
        if normalized.startswith("../") or normalized == "..":
            raise OSError(errno.EACCES, "path outside fixture")
        return os.path.join(self.root, *normalized.split("/"))

    def canonicalize(self, path):
        if not os.path.exists(os.path.join(self.root, ".fixture-canonical-parent")):
            return super().canonicalize(path)
        normalized = posixpath.normpath("/" + path).lstrip("/")
        if normalized == "virtual-root" or normalized.startswith("virtual-root/"):
            return "/" + normalized
        return "/virtual-root" + ("/" + normalized if normalized else "")

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self._local(path)))
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def lstat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.lstat(self._local(path)))
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def list_folder(self, path):
        try:
            injected = os.path.join(self.root, ".fixture-listing.json")
            if os.path.exists(injected):
                with open(injected, encoding="utf-8") as source:
                    name = json.load(source)["name"]
                attributes = paramiko.SFTPAttributes.from_stat(os.stat(os.path.join(self.root, "same.txt")))
                attributes.filename = name
                return [attributes]
            entries = []
            for name in (".", ".."):
                attributes = paramiko.SFTPAttributes.from_stat(os.stat(self._local(path)))
                attributes.filename = name
                entries.append(attributes)
            for name in os.listdir(self._local(path)):
                if name == ".fixture-canonical-parent":
                    continue
                local = os.path.join(self._local(path), name)
                attributes = paramiko.SFTPAttributes.from_stat(os.lstat(local))
                attributes.filename = name
                entries.append(attributes)
            return entries
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def open(self, path, flags, attr):
        try:
            if flags & (os.O_WRONLY | os.O_RDWR) and os.path.exists(os.path.join(self.root, "fail-first-write")):
                os.unlink(os.path.join(self.root, "fail-first-write"))
                self.server.transport.close()
                return paramiko.SFTP_FAILURE
            local = self._local(path)
            descriptor = os.open(local, flags, 0o600)
            mode = "r+b" if flags & os.O_RDWR else "wb" if flags & os.O_WRONLY else "rb"
            handle = FileHandle(flags, path, self.root)
            stream = os.fdopen(descriptor, mode, buffering=0)
            handle.readfile = stream
            handle.writefile = stream
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def remove(self, path):
        try:
            os.remove(self._local(path))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def rmdir(self, path):
        try:
            os.rmdir(self._local(path))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def rename(self, oldpath, newpath):
        try:
            # Windows rename refuses an existing destination, matching SFTP v3.
            # Only the separate POSIX extension is allowed to replace a target.
            os.rename(self._local(oldpath), self._local(newpath))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def posix_rename(self, oldpath, newpath):
        try:
            os.replace(self._local(oldpath), self._local(newpath))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)


def serve_connection(connection, host_key, root, label):
    transport = paramiko.Transport(connection)
    try:
        auth = Authentication(label)
        auth.transport = transport
        transport.add_server_key(host_key)
        transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Storage, root=root)
        transport.start_server(server=auth)
        while transport.is_active():
            time.sleep(0.05)
    finally:
        transport.close()


def main():
    root = os.path.abspath(sys.argv[1])
    label = sys.argv[2]
    os.makedirs(root, exist_ok=True)
    host_key = paramiko.RSAKey.generate(2048)
    fingerprint = base64.b64encode(hashlib.sha256(host_key.asbytes()).digest()).decode("ascii").rstrip("=")
    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(8)
    print(json.dumps({"port": listener.getsockname()[1], "sha256": fingerprint}), flush=True)
    while True:
        connection, _ = listener.accept()
        threading.Thread(target=serve_connection, args=(connection, host_key, root, label), daemon=True).start()


if __name__ == "__main__":
    main()
