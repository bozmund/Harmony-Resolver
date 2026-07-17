/**
 * Harmony Resolver diagnostics, exposed as pi tools.
 *
 * pi has no built-in MCP client, so this spawns the existing stdio bridge
 * (src/Harmony.Resolver.McpBridge) as a subprocess and speaks the same
 * hand-rolled JSON-RPC exchange used by Harmony-Music's
 * harmony-plan-agent-mode package for its own MCP server.
 */

import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import * as readline from "node:readline";
import { Type } from "typebox";
import { defineTool, type ExtensionAPI } from "@earendil-works/pi-coding-agent";

const MCP_BRIDGE_PROJECT = "C:/MyRepositories/Harmony-Resolver/src/Harmony.Resolver.McpBridge";

interface McpResponse {
	jsonrpc: "2.0";
	id?: number;
	result?: {
		content?: Array<{ type: string; text?: string }>;
		isError?: boolean;
	};
	error?: { code: number; message: string };
}

function truncateText(value: string, maxChars: number): string {
	if (value.length <= maxChars) return value;
	return `${value.slice(0, maxChars)}\n\n[truncated to ${maxChars} characters]`;
}

async function callMcpTool(name: string, args: Record<string, unknown>): Promise<{ text: string; isError: boolean }> {
	let nextId = 1;
	const pending = new Map<number, (response: McpResponse) => void>();
	const child: ChildProcessWithoutNullStreams = spawn("dotnet", ["run", "--project", MCP_BRIDGE_PROJECT], {
		windowsHide: true,
	});

	const rl = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
	rl.on("line", (line) => {
		try {
			const response = JSON.parse(line) as McpResponse;
			if (typeof response.id === "number") {
				pending.get(response.id)?.(response);
				pending.delete(response.id);
			}
		} catch {
			// Ignore non-JSON server output.
		}
	});

	let stderr = "";
	child.stderr.on("data", (chunk) => {
		stderr = truncateText(`${stderr}${chunk.toString()}`, 20000);
	});

	const request = (method: string, params?: unknown, timeoutMs = 30000): Promise<McpResponse> => {
		const id = nextId++;
		const payload = { jsonrpc: "2.0", id, method, params };
		return new Promise((resolve, reject) => {
			const timer = setTimeout(() => {
				pending.delete(id);
				reject(new Error(`MCP request timed out: ${method}`));
			}, timeoutMs);
			pending.set(id, (response) => {
				clearTimeout(timer);
				resolve(response);
			});
			child.stdin.write(`${JSON.stringify(payload)}\n`);
		});
	};

	try {
		await request("initialize", {}, 30000);
		await request("tools/list", {}, 30000);
		const response = await request("tools/call", { name, arguments: args }, 30000);

		if (response.error) {
			return { text: `MCP error ${response.error.code}: ${response.error.message}`, isError: true };
		}
		const text = response.result?.content?.map((item) => item.text ?? "").join("\n") ?? "";
		return { text: stderr ? `${text}\n\nserver stderr:\n${stderr}` : text, isError: response.result?.isError === true };
	} finally {
		rl.close();
		child.kill();
	}
}

function diagnosticTool(name: string, label: string, description: string) {
	return defineTool({
		name,
		label,
		description,
		promptSnippet: description,
		parameters: Type.Object({}),
		async execute() {
			const result = await callMcpTool(name, {});
			return { content: [{ type: "text", text: result.text }], isError: result.isError };
		},
	});
}

const systemSnapshotTool = diagnosticTool("get_system_snapshot", "Get System Snapshot", "Returns a sanitized resolver snapshot.");
const dependencyHealthTool = diagnosticTool("get_dependency_health", "Get Dependency Health", "Returns dependency health.");
const queryMetricsTool = diagnosticTool("query_metrics", "Query Metrics", "Queries resolver metrics.");
const deploymentInfoTool = diagnosticTool("get_deployment_info", "Get Deployment Info", "Gets deployment information.");
const diagnosticCheckTool = diagnosticTool("run_diagnostic_check", "Run Diagnostic Check", "Runs standard diagnostics.");

const failedIngestionsTool = defineTool({
	name: "list_failed_ingestions",
	label: "List Failed Ingestions",
	description: "Lists bounded recent failures.",
	promptSnippet: "Lists bounded recent failures.",
	parameters: Type.Object({
		limit: Type.Number({ description: "Maximum number of failures to return." }),
	}),
	async execute(_toolCallId, params) {
		const result = await callMcpTool("list_failed_ingestions", { limit: params.limit });
		return { content: [{ type: "text", text: result.text }], isError: result.isError };
	},
});

const inspectIngestionTool = defineTool({
	name: "inspect_ingestion",
	label: "Inspect Ingestion",
	description: "Inspects one ingestion.",
	promptSnippet: "Inspects one ingestion by video ID.",
	parameters: Type.Object({
		videoId: Type.String({ description: "YouTube video ID." }),
	}),
	async execute(_toolCallId, params) {
		const result = await callMcpTool("inspect_ingestion", { videoId: params.videoId });
		return { content: [{ type: "text", text: result.text }], isError: result.isError };
	},
});

const inspectTrackTool = defineTool({
	name: "inspect_track",
	label: "Inspect Track",
	description: "Inspects one track.",
	promptSnippet: "Inspects one track by video ID.",
	parameters: Type.Object({
		videoId: Type.String({ description: "YouTube video ID." }),
	}),
	async execute(_toolCallId, params) {
		const result = await callMcpTool("inspect_track", { videoId: params.videoId });
		return { content: [{ type: "text", text: result.text }], isError: result.isError };
	},
});

const queryLogsTool = defineTool({
	name: "query_logs",
	label: "Query Logs",
	description: "Queries bounded logs.",
	promptSnippet: "Queries bounded logs over a recent time window.",
	parameters: Type.Object({
		hours: Type.Number({ description: "How many hours back to query." }),
		limit: Type.Number({ description: "Maximum number of log lines to return." }),
	}),
	async execute(_toolCallId, params) {
		const result = await callMcpTool("query_logs", { hours: params.hours, limit: params.limit });
		return { content: [{ type: "text", text: result.text }], isError: result.isError };
	},
});

const getTraceTool = defineTool({
	name: "get_trace",
	label: "Get Trace",
	description: "Gets trace correlation details.",
	promptSnippet: "Gets trace correlation details by trace ID.",
	parameters: Type.Object({
		traceId: Type.String({ description: "Trace ID to inspect." }),
	}),
	async execute(_toolCallId, params) {
		const result = await callMcpTool("get_trace", { traceId: params.traceId });
		return { content: [{ type: "text", text: result.text }], isError: result.isError };
	},
});

export default function harmonyResolverDiagnostics(pi: ExtensionAPI): void {
	pi.registerTool(systemSnapshotTool);
	pi.registerTool(dependencyHealthTool);
	pi.registerTool(failedIngestionsTool);
	pi.registerTool(inspectIngestionTool);
	pi.registerTool(inspectTrackTool);
	pi.registerTool(queryLogsTool);
	pi.registerTool(getTraceTool);
	pi.registerTool(queryMetricsTool);
	pi.registerTool(deploymentInfoTool);
	pi.registerTool(diagnosticCheckTool);
}
