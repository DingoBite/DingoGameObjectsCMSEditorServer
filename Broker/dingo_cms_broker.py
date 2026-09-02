#!/usr/bin/env python3
"""Standalone loopback broker for DingoCMS Unity Editor sessions."""

from __future__ import annotations

import argparse
import base64
import copy
import hashlib
import hmac
import json
import logging
import os
import secrets
import signal
import struct
import threading
import time
import urllib.parse
import uuid
from http import HTTPStatus
from http.cookies import SimpleCookie
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


PROTOCOL_VERSION = "2025-11-25"
SERVER_VERSION = "0.3.0"
BRIDGE_PROTOCOL_VERSION = 1
INSTANCES_RESOURCE_URI = "dingocms://instances"
INSTANCE_ARGUMENT = "dingo_cms_instance"
SESSION_LEASE_SECONDS = 20
REMOTE_REQUEST_TIMEOUT_SECONDS = 60
MAX_REQUEST_BODY_BYTES = 32 * 1024 * 1024
MAX_BROWSER_SESSIONS = 32
TOKEN_ENVIRONMENT_VARIABLE = "DINGO_CMS_EDITOR_TOKEN"
INSTANCE_TOKEN_ENVIRONMENT_VARIABLE = "DINGO_CMS_EDITOR_INSTANCE_TOKEN"
BROKER_FINGERPRINT_ENVIRONMENT_VARIABLE = "DINGO_CMS_EDITOR_BROKER_FINGERPRINT"
INSTANCE_TOKEN_HEADER = "X-DingoCMS-Instance-Token"
SESSION_COOKIE_NAME = "DingoCmsEditorSession"
WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class BrokerError(Exception):
    def __init__(self, code: str, message: str, details=None):
        super().__init__(message)
        self.code = code or "operation_failed"
        self.details = details


class PendingRequest:
    def __init__(self, operation: str, arguments: dict):
        self.request_id = str(uuid.uuid4())
        self.operation = operation
        self.arguments = arguments
        self.event = threading.Event()
        self.result = None
        self.error = None

    def describe(self) -> dict:
        return {
            "request_id": self.request_id,
            "operation": self.operation,
            "arguments": self.arguments,
        }


class SessionRecord:
    def __init__(
        self,
        body: dict,
        session_id: str,
        instance_id: str,
        websocket=None,
    ):
        self.session_id = session_id
        self.instance_id = instance_id
        self.project_name = require_text(body, "project_name")
        self.project_id = require_text(
            body, "project_id", body.get("project_hash")
        )
        self.project_path = normalize_path(require_text(body, "project_path"))
        self.assets_root = normalize_path(require_text(body, "assets_root"))
        self.unity_version = normalize_optional(body.get("unity_version"))
        self.connected_at = utc_timestamp()
        self.last_seen = time.monotonic()
        self.last_seen_timestamp = self.connected_at
        self.pending = {}
        self.websocket = websocket

    def describe(self) -> dict:
        return {
            "id": self.instance_id,
            "name": self.project_name,
            "hash": self.project_id,
            "unity_version": self.unity_version,
            "connected_at": self.connected_at,
            "session_id": self.session_id,
            "projectName": self.project_name,
            "projectId": self.project_id,
            "projectPath": self.project_path,
            "assetsRoot": self.assets_root,
            "unityVersion": self.unity_version,
            "sessionCount": 1,
            "sessionIds": [self.session_id],
            "persistent": False,
            "connectedUtc": self.connected_at,
            "lastConnectedUtc": self.last_seen_timestamp,
        }


class SessionRegistry:
    def __init__(self):
        self._lock = threading.RLock()
        self._instances = {}
        self._sessions = {}
        self._tool_template = []

    def register(self, body: dict, websocket=None) -> dict:
        project_name = require_text(body, "project_name")
        project_id = require_text(
            body, "project_id", body.get("project_hash")
        )
        instance_id = build_instance_id(project_name, project_id)
        session_id = str(uuid.uuid4())
        with self._lock:
            self._prune_locked()
            existing = self._instances.get(instance_id.casefold())
            if existing is not None:
                requested_assets = normalize_path(
                    require_text(body, "assets_root")
                )
                if (
                    existing.assets_root.casefold()
                    != requested_assets.casefold()
                    or existing.project_id.casefold() != project_id.casefold()
                ):
                    raise BrokerError(
                        "project_identity_conflict",
                        f"DingoCMS instance '{instance_id}' is already "
                        "registered for another project path.",
                        {
                            "instance": instance_id,
                            "registeredAssetsRoot": existing.assets_root,
                            "requestedAssetsRoot": requested_assets,
                        },
                    )
                self._remove_locked(
                    existing,
                    "session_replaced",
                    "The Unity project opened a replacement DingoCMS session.",
                )

            record = SessionRecord(
                body,
                session_id,
                instance_id,
                websocket=websocket,
            )
            self._instances[instance_id.casefold()] = record
            self._sessions[session_id] = record
            tools = body.get("tools")
            if isinstance(tools, list) and tools:
                self._tool_template = copy.deepcopy(tools)
            return {
                "session_id": session_id,
                "instance_id": instance_id,
                "lease_seconds": SESSION_LEASE_SECONDS,
            }

    def register_tools(self, session_id: str, tools):
        with self._lock:
            record = self._sessions.get(session_id or "")
            if record is None:
                raise BrokerError(
                    "session_not_found",
                    "The DingoCMS project session has expired or disconnected.",
                )
            if not isinstance(tools, list):
                raise BrokerError(
                    "invalid_request",
                    "'tools' must be an array.",
                )
            self._tool_template = copy.deepcopy(tools)
            self._touch(record)

    def heartbeat(self, session_id: str) -> bool:
        with self._lock:
            self._prune_locked()
            record = self._sessions.get(session_id or "")
            if record is None:
                return False
            self._touch(record)
            return True

    def respond(self, session_id: str, request_id: str, result, error):
        session_id = require_value(session_id, "session_id")
        request_id = require_value(request_id, "request_id")
        with self._lock:
            self._prune_locked()
            record = self._sessions.get(session_id)
            if record is None:
                raise BrokerError(
                    "session_not_found",
                    "The DingoCMS project session has expired or disconnected.",
                )
            pending = record.pending.pop(request_id, None)
            if pending is None:
                raise BrokerError(
                    "request_not_found",
                    f"DingoCMS request '{request_id}' is no longer pending.",
                )
            self._touch(record)
            pending.result = result if isinstance(result, dict) else {}
            pending.error = error if isinstance(error, dict) else None
            pending.event.set()

    def disconnect(self, session_id: str):
        if not session_id:
            return
        with self._lock:
            record = self._sessions.get(session_id)
            if record is not None:
                self._remove_locked(
                    record,
                    "session_disconnected",
                    "The target Unity project disconnected from DingoCMS.",
                )

    def route(self, operation: str, arguments: dict) -> dict:
        operation = require_value(operation, "operation")
        routed_arguments = copy.deepcopy(arguments or {})
        selector = routed_arguments.pop(INSTANCE_ARGUMENT, None)
        with self._lock:
            self._prune_locked()
            record = self._resolve_locked(selector)
            pending = PendingRequest(operation, routed_arguments)
            record.pending[pending.request_id] = pending
            websocket = record.websocket
            if websocket is None:
                record.pending.pop(pending.request_id, None)
                raise BrokerError(
                    "session_disconnected",
                    "The target Unity project has no active WebSocket bridge.",
                )

        try:
            websocket.send_websocket_json(
                {
                    "type": "execute",
                    "id": pending.request_id,
                    "name": operation,
                    "params": routed_arguments,
                    "timeout": REMOTE_REQUEST_TIMEOUT_SECONDS,
                }
            )
        except Exception as exception:
            with self._lock:
                current = record.pending.get(pending.request_id)
                if current is pending:
                    record.pending.pop(pending.request_id, None)
                self._remove_locked(
                    record,
                    "session_disconnected",
                    "The target Unity project disconnected from DingoCMS.",
                )
            raise BrokerError(
                "session_disconnected",
                "The target Unity project disconnected from DingoCMS.",
            ) from exception

        if not pending.event.wait(REMOTE_REQUEST_TIMEOUT_SECONDS):
            with self._lock:
                current = record.pending.get(pending.request_id)
                if current is pending:
                    record.pending.pop(pending.request_id, None)
            raise BrokerError(
                "instance_timeout",
                f"DingoCMS instance '{record.instance_id}' did not answer "
                f"within {REMOTE_REQUEST_TIMEOUT_SECONDS} seconds.",
            )

        if pending.error is not None:
            raise BrokerError(
                pending.error.get("code") or "operation_failed",
                pending.error.get("message")
                or "The Unity project could not execute the DingoCMS request.",
                pending.error.get("details"),
            )
        return pending.result or {}

    def describe_instances(self) -> list:
        with self._lock:
            self._prune_locked()
            return [
                record.describe()
                for record in sorted(
                    self._instances.values(),
                    key=lambda item: item.instance_id.casefold(),
                )
            ]

    def contains_instance(self, selector: str) -> bool:
        if not selector:
            return False
        with self._lock:
            self._prune_locked()
            try:
                self._resolve_locked(selector)
                return True
            except BrokerError:
                return False

    def describe_tools(self) -> list:
        with self._lock:
            tools = copy.deepcopy(self._tool_template)
        for tool in tools:
            if not isinstance(tool, dict):
                continue
            schema = tool.setdefault("inputSchema", {})
            properties = schema.setdefault("properties", {})
            properties[INSTANCE_ARGUMENT] = {
                "type": "string",
                "description": (
                    "Target connected DingoCMS instance. Use "
                    "dingocms://instances to list valid values. Optional "
                    "only while exactly one instance is connected."
                ),
            }
        return tools

    def _resolve_locked(self, selector: str) -> SessionRecord:
        if selector and str(selector).strip():
            value = str(selector).strip()
            record = self._instances.get(value.casefold())
            if record is None:
                record = self._sessions.get(value)
            if record is None:
                for candidate in self._instances.values():
                    if candidate.project_id.casefold() == value.casefold():
                        record = candidate
                        break
            if record is None:
                by_name = [
                    candidate
                    for candidate in self._instances.values()
                    if candidate.project_name.casefold() == value.casefold()
                ]
                if len(by_name) == 1:
                    record = by_name[0]
            if record is not None:
                return record
            raise BrokerError(
                "instance_not_found",
                f"DingoCMS instance '{value}' is not connected.",
                {"instances": self.describe_instances()},
            )

        records = list(self._instances.values())
        if len(records) == 1:
            return records[0]
        if not records:
            raise BrokerError(
                "no_connected_instance",
                "No Unity project is connected to the DingoCMS server.",
                {"instances": []},
            )
        raise BrokerError(
            "instance_required",
            f"{INSTANCE_ARGUMENT} is required while multiple Unity projects "
            "are connected.",
            {"instances": self.describe_instances()},
        )

    def _prune_locked(self):
        cutoff = time.monotonic() - SESSION_LEASE_SECONDS
        expired = [
            record
            for record in self._sessions.values()
            if record.last_seen < cutoff
        ]
        for record in expired:
            self._remove_locked(
                record,
                "session_expired",
                "The target Unity project stopped responding to DingoCMS.",
            )

    def _remove_locked(self, record: SessionRecord, code: str, message: str):
        self._sessions.pop(record.session_id, None)
        current = self._instances.get(record.instance_id.casefold())
        if current is record:
            self._instances.pop(record.instance_id.casefold(), None)
        for pending in list(record.pending.values()):
            pending.error = {"code": code, "message": message}
            pending.event.set()
        record.pending.clear()
        record.websocket = None

    @staticmethod
    def _touch(record: SessionRecord):
        record.last_seen = time.monotonic()
        record.last_seen_timestamp = utc_timestamp()


class BrokerHttpServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(
        self,
        port: int,
        bearer_token: str,
        instance_token: str,
        broker_fingerprint: str,
        web_html: str,
    ):
        self.port = port
        self.origin = f"http://127.0.0.1:{port}"
        self.bearer_token = bearer_token
        self.instance_token = instance_token
        self.broker_fingerprint = broker_fingerprint
        self.web_html = web_html
        self.registry = SessionRegistry()
        self.authentication_lock = threading.RLock()
        self.browser_sessions = {}
        self.bootstrap_tokens = {}
        super().__init__(("127.0.0.1", port), BrokerRequestHandler)

    def create_bootstrap_url(self, instance: str) -> str:
        token = secrets.token_hex(32)
        with self.authentication_lock:
            self.bootstrap_tokens[token] = instance
        return self.origin + "/?token=" + urllib.parse.quote(token, safe="")

    def consume_bootstrap(self, token: str):
        with self.authentication_lock:
            instance = self.bootstrap_tokens.pop(token, None)
            if instance is None:
                return None
            browser_token = secrets.token_hex(32)
            if len(self.browser_sessions) >= MAX_BROWSER_SESSIONS:
                oldest = next(iter(self.browser_sessions))
                self.browser_sessions.pop(oldest, None)
            self.browser_sessions[browser_token] = instance
            return browser_token


class BrokerRequestHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, message, *args):
        logging.info("%s - %s", self.address_string(), message % args)

    def do_OPTIONS(self):
        if not self._origin_is_exact():
            self._send_error(
                HTTPStatus.FORBIDDEN,
                "forbidden_origin",
                "Preflight requests require the server's exact origin.",
            )
            return
        self.send_response(HTTPStatus.NO_CONTENT)
        self._add_cors_headers()
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header(
            "Access-Control-Allow-Headers",
            "Authorization, Content-Type, MCP-Protocol-Version, "
            f"MCP-Session-Id, {INSTANCE_TOKEN_HEADER}",
        )
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_GET(self):
        try:
            if not self._origin_is_allowed():
                self._send_error(
                    HTTPStatus.FORBIDDEN,
                    "forbidden_origin",
                    "The request origin is not allowed.",
                )
                return
            path, query = self._path_and_query()
            if path == "/hub/plugin":
                if not self._is_bearer_authenticated():
                    self._send_unauthorized()
                    return
                self._handle_plugin_websocket()
                return
            if path == "/":
                self._handle_web_index(query)
                return
            if path == "/server":
                if not self._is_bearer_authenticated():
                    self._send_unauthorized()
                    return
                self._send_json(
                    HTTPStatus.OK,
                    {
                        "ok": True,
                        "processId": os.getpid(),
                        "port": self.server.port,
                        "serverVersion": SERVER_VERSION,
                        "bridgeProtocolVersion": BRIDGE_PROTOCOL_VERSION,
                        "brokerFingerprint": self.server.broker_fingerprint,
                        "instances": self.server.registry.describe_instances(),
                    },
                )
                return
            if path == "/health":
                if (
                    not self._is_bearer_authenticated()
                    or not self._is_expected_instance()
                ):
                    self._send_unauthorized()
                    return
                self._send_json(
                    HTTPStatus.OK,
                    {
                        "ok": True,
                        "processId": os.getpid(),
                        "port": self.server.port,
                        "instanceToken": self.server.instance_token,
                        "serverVersion": SERVER_VERSION,
                        "bridgeProtocolVersion": BRIDGE_PROTOCOL_VERSION,
                        "brokerFingerprint": self.server.broker_fingerprint,
                        "connectedInstances": len(
                            self.server.registry.describe_instances()
                        ),
                    },
                )
                return
            self._send_error(
                HTTPStatus.NOT_FOUND,
                "not_found",
                "The requested endpoint does not exist.",
            )
        except Exception as exception:
            self._send_unexpected(exception)

    def do_POST(self):
        try:
            if not self._origin_is_allowed():
                self._send_error(
                    HTTPStatus.FORBIDDEN,
                    "forbidden_origin",
                    "The request origin is not allowed.",
                )
                return
            path, _ = self._path_and_query()
            if path == "/web/bootstrap":
                if not self._is_bearer_authenticated():
                    self._send_unauthorized()
                    return
                self._handle_web_bootstrap()
                return
            if path == "/mcp":
                if not self._is_authenticated():
                    self._send_unauthorized()
                    return
                self._handle_mcp()
                return
            if path.startswith("/api/"):
                if not self._is_authenticated():
                    self._send_unauthorized()
                    return
                operation = urllib.parse.unquote(path[len("/api/") :])
                if not operation or "/" in operation:
                    self._send_error(
                        HTTPStatus.BAD_REQUEST,
                        "invalid_operation",
                        "The API operation must be one non-empty path segment.",
                    )
                    return
                self._handle_api(operation)
                return
            self._send_error(
                HTTPStatus.NOT_FOUND,
                "not_found",
                "The requested endpoint does not exist.",
            )
        except BrokerError as exception:
            self._send_broker_error(exception)
        except Exception as exception:
            self._send_unexpected(exception)

    def _handle_plugin_websocket(self):
        upgrade = (self.headers.get("Upgrade") or "").lower()
        connection = (self.headers.get("Connection") or "").lower()
        websocket_key = (self.headers.get("Sec-WebSocket-Key") or "").strip()
        websocket_version = (
            self.headers.get("Sec-WebSocket-Version") or ""
        ).strip()
        if (
            upgrade != "websocket"
            or "upgrade" not in connection
            or not websocket_key
            or websocket_version != "13"
        ):
            self._send_error(
                HTTPStatus.BAD_REQUEST,
                "invalid_websocket_handshake",
                "DingoCMS plugin sessions require a WebSocket upgrade.",
            )
            return

        accept = base64.b64encode(
            hashlib.sha1(
                (websocket_key + WEBSOCKET_GUID).encode("ascii")
            ).digest()
        ).decode("ascii")
        self.send_response(HTTPStatus.SWITCHING_PROTOCOLS)
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.close_connection = True
        self._websocket_send_lock = threading.Lock()
        self._websocket_open = True
        session_id = None
        fragments = bytearray()
        fragment_opcode = None
        self.send_websocket_json(
            {
                "type": "welcome",
                "serverTimeout": REMOTE_REQUEST_TIMEOUT_SECONDS,
                "keepAliveInterval": 15,
            }
        )
        try:
            while self._websocket_open:
                frame = self._read_websocket_frame()
                if frame is None:
                    break
                final, opcode, payload = frame
                if opcode == 0x8:
                    self._send_websocket_frame(0x8, payload[:125])
                    break
                if opcode == 0x9:
                    self._send_websocket_frame(0xA, payload[:125])
                    continue
                if opcode == 0xA:
                    continue
                if opcode == 0x1:
                    fragments = bytearray(payload)
                    fragment_opcode = opcode
                elif opcode == 0x0 and fragment_opcode is not None:
                    fragments.extend(payload)
                else:
                    continue
                if not final:
                    continue

                try:
                    message = json.loads(fragments.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    self._send_websocket_frame(
                        0x8,
                        struct.pack("!H", 1007) + b"Invalid JSON",
                    )
                    break
                finally:
                    fragments = bytearray()
                    fragment_opcode = None
                if not isinstance(message, dict):
                    continue
                message_type = message.get("type")
                if message_type == "register":
                    if session_id:
                        continue
                    registration = self.server.registry.register(
                        {
                            "project_name": message.get("project_name"),
                            "project_id": message.get("project_hash")
                            or message.get("project_id"),
                            "project_path": message.get("project_path"),
                            "assets_root": message.get("assets_root"),
                            "unity_version": message.get("unity_version"),
                        },
                        websocket=self,
                    )
                    session_id = registration["session_id"]
                    self.send_websocket_json(
                        {
                            "type": "registered",
                            "session_id": session_id,
                            "instance_id": registration["instance_id"],
                        }
                    )
                    threading.Thread(
                        target=self._websocket_ping_loop,
                        daemon=True,
                    ).start()
                elif message_type == "register_tools" and session_id:
                    self.server.registry.register_tools(
                        session_id,
                        message.get("tools"),
                    )
                elif message_type == "command_result" and session_id:
                    self.server.registry.respond(
                        session_id,
                        message.get("id"),
                        message.get("result"),
                        message.get("error"),
                    )
                elif message_type == "pong" and session_id:
                    self.server.registry.heartbeat(session_id)
        except (BrokenPipeError, ConnectionResetError, OSError):
            pass
        except BrokerError as exception:
            logging.warning("Plugin WebSocket error: %s", exception)
            try:
                self._send_websocket_frame(
                    0x8,
                    struct.pack("!H", 1008)
                    + str(exception).encode("utf-8")[:120],
                )
            except OSError:
                pass
        finally:
            self._websocket_open = False
            if session_id:
                self.server.registry.disconnect(session_id)

    def _websocket_ping_loop(self):
        while getattr(self, "_websocket_open", False):
            time.sleep(15)
            if not getattr(self, "_websocket_open", False):
                return
            try:
                self.send_websocket_json({"type": "ping"})
            except (BrokenPipeError, ConnectionResetError, OSError):
                self._websocket_open = False
                return

    def send_websocket_json(self, value: dict):
        payload = json.dumps(
            value,
            separators=(",", ":"),
            ensure_ascii=False,
        ).encode("utf-8")
        self._send_websocket_frame(0x1, payload)

    def _read_websocket_frame(self):
        header = self._read_websocket_exact(2)
        if header is None:
            return None
        first, second = header
        final = bool(first & 0x80)
        opcode = first & 0x0F
        masked = bool(second & 0x80)
        payload_length = second & 0x7F
        if payload_length == 126:
            extended = self._read_websocket_exact(2)
            if extended is None:
                return None
            payload_length = struct.unpack("!H", extended)[0]
        elif payload_length == 127:
            extended = self._read_websocket_exact(8)
            if extended is None:
                return None
            payload_length = struct.unpack("!Q", extended)[0]
        if payload_length > MAX_REQUEST_BODY_BYTES:
            raise BrokerError(
                "request_too_large",
                f"WebSocket message exceeds {MAX_REQUEST_BODY_BYTES} bytes.",
            )
        mask = self._read_websocket_exact(4) if masked else None
        if masked and mask is None:
            return None
        payload = self._read_websocket_exact(payload_length)
        if payload is None:
            return None
        if masked:
            payload = bytes(
                value ^ mask[index % 4]
                for index, value in enumerate(payload)
            )
        return final, opcode, payload

    def _read_websocket_exact(self, size: int):
        if size == 0:
            return b""
        result = bytearray()
        while len(result) < size:
            chunk = self.rfile.read(size - len(result))
            if not chunk:
                return None
            result.extend(chunk)
        return bytes(result)

    def _send_websocket_frame(self, opcode: int, payload: bytes):
        if not getattr(self, "_websocket_open", False):
            raise ConnectionResetError("Plugin WebSocket is closed.")
        payload = payload or b""
        length = len(payload)
        if length < 126:
            header = bytes((0x80 | opcode, length))
        elif length <= 0xFFFF:
            header = bytes((0x80 | opcode, 126)) + struct.pack("!H", length)
        else:
            header = bytes((0x80 | opcode, 127)) + struct.pack("!Q", length)
        with self._websocket_send_lock:
            self.connection.sendall(header + payload)

    def _handle_web_index(self, query: dict):
        bootstrap_token = first_query_value(query, "token")
        if bootstrap_token:
            browser_token = self.server.consume_bootstrap(bootstrap_token)
            if browser_token is None:
                self._send_error(
                    HTTPStatus.UNAUTHORIZED,
                    "invalid_bootstrap_token",
                    "The browser bootstrap token is invalid or already used.",
                )
                return
            self.send_response(HTTPStatus.SEE_OTHER)
            self._add_cors_headers()
            self.send_header(
                "Set-Cookie",
                f"{SESSION_COOKIE_NAME}={browser_token}; Path=/; "
                "HttpOnly; SameSite=Strict",
            )
            self.send_header("Location", "/")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self._send_bytes(
            HTTPStatus.OK,
            "text/html; charset=utf-8",
            self.server.web_html.encode("utf-8"),
            {
                "Cache-Control": "no-store",
                "Content-Security-Policy": (
                    "default-src 'none'; style-src 'unsafe-inline'; "
                    "script-src 'unsafe-inline'; connect-src 'self'; "
                    "base-uri 'none'; frame-ancestors 'none'"
                ),
                "Referrer-Policy": "no-referrer",
                "X-Content-Type-Options": "nosniff",
            },
        )

    def _handle_web_bootstrap(self):
        body = self._read_json()
        instance = body.get(INSTANCE_ARGUMENT)
        if not instance or not self.server.registry.contains_instance(instance):
            self._send_error(
                HTTPStatus.NOT_FOUND,
                "instance_not_found",
                "The requested DingoCMS instance is not connected.",
                {"instances": self.server.registry.describe_instances()},
            )
            return
        self._send_json(
            HTTPStatus.OK,
            {
                "ok": True,
                "url": self.server.create_bootstrap_url(str(instance)),
            },
        )

    def _handle_api(self, operation: str):
        arguments = self._read_json()
        instance = self._browser_instance()
        if instance and INSTANCE_ARGUMENT not in arguments:
            arguments[INSTANCE_ARGUMENT] = instance
        try:
            result = self.server.registry.route(operation, arguments)
            self._send_json(HTTPStatus.OK, {"ok": True, "result": result})
        except BrokerError as exception:
            self._send_broker_error(exception, map_operation_status(exception.code))

    def _handle_mcp(self):
        try:
            request = self._read_json()
        except ValueError as exception:
            self._send_json(
                HTTPStatus.OK,
                json_rpc_error(None, -32700, "Parse error", str(exception)),
            )
            return
        request_id = copy.deepcopy(request.get("id"))
        is_notification = "id" not in request
        method = request.get("method")
        if request.get("jsonrpc") != "2.0" or not isinstance(method, str):
            if is_notification:
                self._send_empty(HTTPStatus.ACCEPTED)
            else:
                self._send_json(
                    HTTPStatus.OK,
                    json_rpc_error(
                        request_id,
                        -32600,
                        "Invalid Request",
                        "Expected a JSON-RPC 2.0 request with a string method.",
                    ),
                )
            return
        protocol_version = self.headers.get("MCP-Protocol-Version")
        if (
            method != "initialize"
            and protocol_version
            and protocol_version != PROTOCOL_VERSION
        ):
            self._send_error(
                HTTPStatus.BAD_REQUEST,
                "unsupported_protocol_version",
                f"MCP-Protocol-Version '{protocol_version}' is unsupported; "
                f"expected '{PROTOCOL_VERSION}'.",
            )
            return
        if is_notification:
            self._send_empty(HTTPStatus.ACCEPTED)
            return

        if method == "initialize":
            result = {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {
                    "tools": {"listChanged": False},
                    "resources": {"listChanged": False},
                },
                "serverInfo": {
                    "name": "DingoCMS Editor Server",
                    "version": SERVER_VERSION,
                },
                "instructions": (
                    "Read dingocms://instances before editing. Pass "
                    "dingo_cms_instance when multiple Unity projects are "
                    "connected. Use schema and read operations before editing."
                ),
            }
            response = json_rpc_result(request_id, result)
        elif method == "ping":
            response = json_rpc_result(request_id, {})
        elif method == "tools/list":
            response = json_rpc_result(
                request_id,
                {"tools": self.server.registry.describe_tools()},
            )
        elif method == "tools/call":
            response = self._mcp_tools_call(request_id, request.get("params"))
        elif method == "resources/list":
            response = json_rpc_result(
                request_id,
                {
                    "resources": [
                        {
                            "uri": INSTANCES_RESOURCE_URI,
                            "name": "Connected DingoCMS instances",
                            "description": (
                                "Live Unity projects connected to the shared "
                                "DingoCMS server."
                            ),
                            "mimeType": "application/json",
                        }
                    ]
                },
            )
        elif method == "resources/read":
            response = self._mcp_resources_read(
                request_id, request.get("params")
            )
        else:
            response = json_rpc_error(
                request_id,
                -32601,
                "Method not found",
                f"MCP method '{method}' is not supported.",
            )
        self._send_json(HTTPStatus.OK, response)

    def _mcp_tools_call(self, request_id, parameters):
        parameters = parameters if isinstance(parameters, dict) else {}
        operation = parameters.get("name")
        if not operation or not isinstance(operation, str):
            return json_rpc_error(
                request_id,
                -32602,
                "Invalid params",
                "tools/call requires a non-empty params.name.",
            )
        arguments = parameters.get("arguments", {})
        if not isinstance(arguments, dict):
            return json_rpc_error(
                request_id,
                -32602,
                "Invalid params",
                "tools/call params.arguments must be a JSON object.",
            )
        try:
            structured = self.server.registry.route(operation, arguments)
            is_error = False
        except BrokerError as exception:
            structured = {
                "error": {
                    "code": exception.code,
                    "message": str(exception),
                }
            }
            if exception.details is not None:
                structured["error"]["details"] = exception.details
            is_error = True
        result = {
            "content": [
                {
                    "type": "text",
                    "text": json.dumps(
                        structured,
                        separators=(",", ":"),
                        ensure_ascii=False,
                    ),
                }
            ],
            "structuredContent": structured,
            "isError": is_error,
        }
        return json_rpc_result(request_id, result)

    def _mcp_resources_read(self, request_id, parameters):
        parameters = parameters if isinstance(parameters, dict) else {}
        uri = parameters.get("uri")
        if uri != INSTANCES_RESOURCE_URI:
            return json_rpc_error(
                request_id,
                -32002,
                "Resource not found",
                f"DingoCMS resource '{uri}' does not exist.",
            )
        instances = self.server.registry.describe_instances()
        document = {
            "success": True,
            "transport": "http",
            "instance_count": len(instances),
            "instances": instances,
        }
        return json_rpc_result(
            request_id,
            {
                "contents": [
                    {
                        "uri": INSTANCES_RESOURCE_URI,
                        "mimeType": "application/json",
                        "text": json.dumps(document, indent=2),
                    }
                ]
            },
        )

    def _read_json(self) -> dict:
        content_length = int(self.headers.get("Content-Length") or "0")
        if content_length > MAX_REQUEST_BODY_BYTES:
            raise BrokerError(
                "request_too_large",
                f"Request body exceeds {MAX_REQUEST_BODY_BYTES} bytes.",
            )
        raw = self.rfile.read(content_length)
        if len(raw) > MAX_REQUEST_BODY_BYTES:
            raise BrokerError(
                "request_too_large",
                f"Request body exceeds {MAX_REQUEST_BODY_BYTES} bytes.",
            )
        if not raw.strip():
            return {}
        try:
            value = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exception:
            raise ValueError(str(exception)) from exception
        if not isinstance(value, dict):
            raise ValueError("Expected a JSON object request body.")
        return value

    def _path_and_query(self):
        parsed = urllib.parse.urlsplit(self.path)
        return parsed.path, urllib.parse.parse_qs(parsed.query)

    def _origin_is_allowed(self) -> bool:
        origin = self.headers.get("Origin")
        return not origin or origin == self.server.origin

    def _origin_is_exact(self) -> bool:
        return self.headers.get("Origin") == self.server.origin

    def _is_bearer_authenticated(self) -> bool:
        authorization = self.headers.get("Authorization") or ""
        if not authorization.lower().startswith("bearer "):
            return False
        return tokens_match(
            authorization[len("Bearer ") :], self.server.bearer_token
        )

    def _is_expected_instance(self) -> bool:
        return not self.server.instance_token or tokens_match(
            self.headers.get(INSTANCE_TOKEN_HEADER), self.server.instance_token
        )

    def _is_authenticated(self) -> bool:
        return self._is_bearer_authenticated() or self._browser_instance() is not None

    def _browser_instance(self):
        cookie_header = self.headers.get("Cookie")
        if not cookie_header:
            return None
        cookie = SimpleCookie()
        try:
            cookie.load(cookie_header)
        except Exception:
            return None
        morsel = cookie.get(SESSION_COOKIE_NAME)
        if morsel is None:
            return None
        with self.server.authentication_lock:
            return self.server.browser_sessions.get(morsel.value)

    def _send_unauthorized(self):
        self._send_error(
            HTTPStatus.UNAUTHORIZED,
            "unauthorized",
            "A valid bearer token or browser session is required.",
            extra_headers={
                "WWW-Authenticate": 'Bearer realm="DingoCMS Editor Server"'
            },
        )

    def _send_broker_error(self, exception: BrokerError, status=None):
        self._send_error(
            status or HTTPStatus.CONFLICT,
            exception.code,
            str(exception),
            exception.details,
        )

    def _send_unexpected(self, exception: Exception):
        logging.exception("Unhandled broker request error")
        try:
            self._send_error(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                "internal_error",
                str(exception),
            )
        except (BrokenPipeError, ConnectionResetError):
            pass

    def _send_error(
        self,
        status,
        code: str,
        message: str,
        details=None,
        extra_headers=None,
    ):
        error = {"code": code, "message": message}
        if details is not None:
            error["details"] = details
        self._send_json(
            status,
            {"ok": False, "error": error},
            extra_headers=extra_headers,
        )

    def _send_json(self, status, value, extra_headers=None):
        body = json.dumps(
            value, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
        self._send_bytes(
            status,
            "application/json; charset=utf-8",
            body,
            extra_headers,
        )

    def _send_empty(self, status):
        self.send_response(status)
        self._add_cors_headers()
        self.send_header("Content-Length", "0")
        self.end_headers()

    def _send_bytes(
        self, status, content_type: str, body: bytes, extra_headers=None
    ):
        self.send_response(status)
        self._add_cors_headers()
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for name, value in (extra_headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    def _add_cors_headers(self):
        if self._origin_is_exact():
            self.send_header("Access-Control-Allow-Origin", self.server.origin)
            self.send_header("Access-Control-Allow-Credentials", "true")
            self.send_header("Vary", "Origin")


def require_text(body: dict, name: str, fallback=None) -> str:
    value = body.get(name, fallback)
    return require_value(value, name)


def require_value(value, name: str) -> str:
    normalized = str(value).strip() if value is not None else ""
    if not normalized:
        raise BrokerError("invalid_request", f"'{name}' is required.")
    return normalized


def normalize_optional(value):
    normalized = str(value).strip() if value is not None else ""
    return normalized or None


def normalize_path(value: str) -> str:
    return os.path.normpath(os.path.abspath(value))


def build_instance_id(project_name: str, project_id: str) -> str:
    suffix = "".join(character for character in project_id if character.isalnum())[
        :16
    ]
    if not suffix:
        suffix = hashlib.sha256(project_id.encode("utf-8")).hexdigest()[:16]
    return f"{project_name}@{suffix.lower()}"


def utc_timestamp() -> str:
    milliseconds = int(time.time() * 1000)
    seconds, fraction = divmod(milliseconds, 1000)
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(seconds)) + (
        f".{fraction:03d}Z"
    )


def tokens_match(left, right) -> bool:
    if left is None or right is None:
        return False
    return hmac.compare_digest(str(left), str(right))


def first_query_value(query: dict, name: str):
    values = query.get(name)
    return values[0] if values else None


def json_rpc_result(request_id, result: dict) -> dict:
    return {"jsonrpc": "2.0", "id": request_id, "result": result}


def json_rpc_error(request_id, code: int, message: str, details=None) -> dict:
    error = {"code": code, "message": message}
    if details:
        error["data"] = {"details": details}
    return {"jsonrpc": "2.0", "id": request_id, "error": error}


def map_operation_status(code: str):
    if code == "not_found":
        return HTTPStatus.NOT_FOUND
    if code in {
        "content_conflict",
        "asset_conflict",
        "path_conflict",
        "module_casing_conflict",
        "changeset_not_active",
        "recovery_pending",
        "authoring_locked",
    }:
        return HTTPStatus.CONFLICT
    if code in {
        "invalid_request",
        "invalid_data",
        "protected_path",
        "unknown_operation",
    }:
        return HTTPStatus.BAD_REQUEST
    return HTTPStatus.INTERNAL_SERVER_ERROR


def parse_arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--web-index", required=True)
    parser.add_argument("--log-file", required=True)
    return parser.parse_args()


def main():
    arguments = parse_arguments()
    os.makedirs(os.path.dirname(os.path.abspath(arguments.log_file)), exist_ok=True)
    logging.basicConfig(
        filename=arguments.log_file,
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        encoding="utf-8",
    )
    bearer_token = os.environ.get(TOKEN_ENVIRONMENT_VARIABLE, "").strip()
    if not bearer_token:
        raise RuntimeError(f"{TOKEN_ENVIRONMENT_VARIABLE} is required.")
    instance_token = os.environ.get(
        INSTANCE_TOKEN_ENVIRONMENT_VARIABLE, ""
    ).strip()
    broker_fingerprint = os.environ.get(
        BROKER_FINGERPRINT_ENVIRONMENT_VARIABLE, ""
    ).strip()
    with open(arguments.web_index, "r", encoding="utf-8") as stream:
        web_html = stream.read()

    server = BrokerHttpServer(
        arguments.port,
        bearer_token,
        instance_token,
        broker_fingerprint,
        web_html,
    )

    def request_shutdown(_signum, _frame):
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGINT, request_shutdown)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_shutdown)
    logging.info(
        "DingoCMS standalone broker ready: pid=%s port=%s version=%s",
        os.getpid(),
        arguments.port,
        SERVER_VERSION,
    )
    try:
        server.serve_forever(poll_interval=0.2)
    finally:
        server.server_close()
        logging.info("DingoCMS standalone broker stopped")


if __name__ == "__main__":
    main()
