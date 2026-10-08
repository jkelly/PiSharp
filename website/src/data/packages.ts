export type Package = { id: string; description: string; group: 'Core' | 'Frontends' | 'Extensions'; nuget?: boolean; tool?: boolean; optional?: boolean };

export const packages: Package[] = [
	{ id: 'PiSharp.Contracts', group: 'Core', nuget: true, description: 'Shared message, streaming and JSON contracts used by every other package.' },
	{ id: 'PiSharp.AI', group: 'Core', nuget: true, description: 'Streaming LLM transports for Anthropic, OpenAI, Azure, Google and Mistral, plus the model catalog.' },
	{ id: 'PiSharp.Agent', group: 'Core', nuget: true, description: 'The agent loop: turns, tool invocation and batching, steering and follow-up queues.' },
	{ id: 'PiSharp.Tools', group: 'Core', nuget: true, description: 'Built-in file and process tools with output truncation and safe file mutation.' },
	{ id: 'PiSharp.Sessions', group: 'Core', nuget: true, description: 'Tree-structured session storage, compaction and context building.' },
	{ id: 'PiSharp.CodingAgent', group: 'Core', nuget: true, description: 'The full coding-agent profile: persistent sessions, recovery and tree navigation.' },
	{ id: 'PiSharp.Rpc', group: 'Frontends', nuget: true, description: "Pi's JSON RPC protocol over stdin/stdout, including extension UI requests." },
	{ id: 'PiSharp.Tui', group: 'Frontends', nuget: true, description: 'Terminal UI building blocks: input, text and list components, rendering.' },
	{ id: 'PiSharp.Cli', group: 'Frontends', tool: true, description: 'The command-line app itself, installed as a .NET tool and run as pisharp.' },
	{ id: 'PiSharp.Codemode', group: 'Core', nuget: true, description: 'Codemode: model-written JavaScript in a Jint sandbox that calls the session tools.' },
	{ id: 'PiSharp.Extensions.Abstractions', group: 'Extensions', nuget: true, description: 'The interfaces you implement to write an extension.' },
	{ id: 'PiSharp.Extensions.Runtime', group: 'Extensions', nuget: true, description: 'Discovery, loading, registration and dispatch for hosts.' },
	{ id: 'PiSharp.Extensions.Agent', group: 'Extensions', nuget: true, description: 'Connects extension hooks to the agent loop.' },
	{ id: 'PiSharp.ExtensionHost', group: 'Extensions', nuget: true, description: 'Supervised out-of-process extension workers.' },
	{ id: 'PiSharp.Compatibility.Node', group: 'Extensions', nuget: true, optional: true, description: "Optional bridge for running Pi's TypeScript extensions. Requires Node." },
];
