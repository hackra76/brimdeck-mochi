import net from "node:net";

const rawEvent = (process.argv[2] || "").trim();
const PIPE_PATH = "\\\\.\\pipe\\brimdeck-mochi-agent";

const EVENT_MAP = {
  PreToolUse: "pre_tool_use",
  PostToolUse: "working",
  PreInvocation: "thinking",
  PostInvocation: "finished",
  Stop: "finished",
  Approval: "approval",
};

const mappedEvent = EVENT_MAP[rawEvent] || rawEvent.toLowerCase() || "working";

function readStdinBrief(timeoutMs = 40) {
  return new Promise((resolve) => {
    if (process.stdin.isTTY) {
      resolve("");
      return;
    }
    let data = "";
    const timer = setTimeout(() => {
      process.stdin.removeAllListeners();
      process.stdin.pause();
      resolve(data);
    }, timeoutMs);

    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
      if (data.length > 8192) {
        clearTimeout(timer);
        process.stdin.removeAllListeners();
        process.stdin.pause();
        resolve(data);
      }
    });
    process.stdin.on("end", () => {
      clearTimeout(timer);
      resolve(data);
    });
    process.stdin.on("error", () => {
      clearTimeout(timer);
      resolve(data);
    });
  });
}

function sendToPipe(payload) {
  return new Promise((resolve) => {
    const client = net.connect(PIPE_PATH, () => {
      client.end(JSON.stringify(payload) + "\n", resolve);
    });
    client.setTimeout(200, () => {
      client.destroy();
      resolve();
    });
    client.on("error", () => resolve());
  });
}

const stdinText = await readStdinBrief();
let toolName;
let eventToSend = mappedEvent;

if (stdinText.trim()) {
  try {
    const parsed = JSON.parse(stdinText);
    if (typeof parsed.tool_name === "string" && parsed.tool_name) {
      toolName = parsed.tool_name;
    }
    if (parsed.requires_approval === true || parsed.approval === true) {
      eventToSend = "approval";
    }
  } catch {
    // Ignore non-JSON stdin.
  }
}

await sendToPipe(toolName ? { event: eventToSend, tool_name: toolName } : { event: eventToSend });
