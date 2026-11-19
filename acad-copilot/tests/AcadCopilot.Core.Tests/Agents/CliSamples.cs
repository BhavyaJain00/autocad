namespace AcadCopilot.Core.Tests.Agents;

/// <summary>
/// Recorded CLI output used by the parser tests. Claude Code 2.1.312 and Codex CLI 0.154.0 samples were captured on
/// the development PC (one tiny prompt plus one resume each, with the AutoCAD bridge running without AutoCAD, and runs
/// with an empty config folder for the signed-out case); the Gemini CLI 0.60.0 success sample is built from the
/// event shapes in its stream-json formatter (the CLI is not signed in with Google on that PC), the error line is real.
/// </summary>
internal static class CliSamples
{
    /// <summary>Claude Code: acad_status call (AutoCAD not running), streamed text, result.</summary>
    public const string ClaudeToolCallTurn =
        """
        {"type":"system","subtype":"init","cwd":"C:/Users/u/AppData/Local/AcadCopilot/workspace/claude","session_id":"75a8ea39-878f-446a-a466-a09657aac33f","tools":["mcp__acad__ask_user","mcp__acad__acad_status","mcp__acad__update_plan"],"mcp_servers":[{"name":"acad","status":"connected","source":"dynamic"}],"model":"claude-haiku-5-5","permissionMode":"dontAsk","apiKeySource":"none","claude_code_version":"2.1.312","uuid":"97d7dfa6-9f00-425d-809b-baac0ce9a902"}
        {"type":"system","subtype":"status","status":"requesting","session_id":"75a8ea39-878f-446a-a466-a09657aac33f","uuid":"b3988170-d849-48db-aa26-b9e8f5f12ff9"}
        {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-haiku-5-5","id":"msg_011Ch2K2ZVoHjQLnRpVz41MD","type":"message","role":"assistant","content":[]}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"2325294b-64ed-42f5-96f1-8d7c560f02e2","ttft_ms":1671,"thinking_display":"updates","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_017MW8wBTpSFJCYA59bCgf5c","name":"mcp__acad__acad_status","input":{},"caller":{"type":"direct"}}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"67e4c21e-c0a9-45a3-81a5-d60d07099a26","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":""}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"9842c40d-097c-4108-a740-d5a0a9ba5e46","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_011Ch2K2ZVoHjQLnRpVz41MD","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_017MW8wBTpSFJCYA59bCgf5c","name":"mcp__acad__acad_status","input":{},"caller":{"type":"direct"}}],"container":null,"stop_reason":null,"stop_sequence":null,"stop_details":null,"input_transformations":[],"diagnostics":null,"context_management":null},"parent_tool_use_id":null,"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","uuid":"cb4b9437-fafe-403a-9b2b-14eef5579fc8","timestamp":"2026-11-14T06:01:42.437Z","tool_use_meta":[{"id":"toolu_017MW8wBTpSFJCYA59bCgf5c","display_name":"AutoCAD connection status","server_display_name":"acad","tool_name":"acad_status"}]}
        {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"ec17f8da-a177-467e-a698-1dd02af8d83f","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null,"stop_details":null,"container":null},"usage":{"output_tokens":34}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"fb870cac-c609-43f9-b4fb-c79f8350f8fc","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"stream_event","event":{"type":"message_stop"},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"18029995-b44a-4c62-940b-74171fee5447","api_message_id":"msg_011Ch2K2ZVoHjQLnRpVz41MD"}
        {"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_017MW8wBTpSFJCYA59bCgf5c","type":"tool_result","content":[{"type":"text","text":"{\"connected\":false,\"error\":\"No running AutoCAD Architecture 2027 with the AI Copilot add-in was found. Ask the user to start AutoCAD Architecture 2027 (the add-in loads automatically) and try again.\",\"panelTools\":true}"}]}]},"parent_tool_use_id":null,"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","uuid":"3301b182-a2e9-40e9-a063-1488a0cfec8e","timestamp":"2026-11-14T06:01:42.529Z","tool_result_meta":[{"id":"toolu_017MW8wBTpSFJCYA59bCgf5c","permission_decision":{"decision":"accept","source":"config","reason_type":"rule"}}]}
        {"type":"system","subtype":"status","status":"requesting","session_id":"75a8ea39-878f-446a-a466-a09657aac33f","uuid":"300f0615-344d-4c7d-bac1-4a58234a877f"}
        {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-haiku-5-5","id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL","type":"message","role":"assistant","content":[]}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"ad12c5d5-7691-4bfb-9319-91dead382a4d","ttft_ms":1606,"thinking_display":"updates","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"e4d6a47c-3d4e-4dcd-ac92-6d7076e90e5c","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"AutoCAD is"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"97b7de90-bac9-44e1-af93-2a623f8b66db","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"n't conn"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"1e23916c-c4b1-4377-b684-f58313b510c3","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ected: no"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"32940c6a-cb8b-41a7-8dc2-7afb80e491c0","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" running AutoCAD Architecture 2027 "}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"1d9589e5-b47a-4c18-a07f-a83e3b6e3396","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"with the AI Copilot"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"dde2fc88-1564-446d-9d7c-3eeab9703153","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" add-in was found. Please"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"a88bf6fa-1a39-4da1-82cc-82fcc5afcbe6","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" start AutoCAD Architecture 2027 "}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"1c4e4132-64af-4a4e-9926-442406b4b02d","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"so"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"a7a4d5be-e68b-4bb4-820d-855bc9a5c016","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" the add-in loads,"}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"ece85c03-2d66-4995-8f31-660921419f11","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" then "}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"7bf5e4f1-8e45-4ce8-8716-d1d125aaf7b7","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ask me again."}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"cb310905-6a73-480e-9c03-f1bdedea8db8","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"assistant","message":{"model":"claude-haiku-5-5","id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL","type":"message","role":"assistant","content":[{"type":"text","text":"AutoCAD isn't connected: no running AutoCAD Architecture 2027 with the AI Copilot add-in was found. Please start AutoCAD Architecture 2027 so the add-in loads, then ask me again."}],"container":null,"stop_reason":null,"stop_sequence":null,"stop_details":null,"input_transformations":[],"diagnostics":null,"context_management":null},"parent_tool_use_id":null,"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","uuid":"b566fde9-e110-4c91-bff6-ab121b31d587","timestamp":"2026-11-14T06:01:44.393Z"}
        {"type":"stream_event","event":{"type":"content_block_stop","index":0},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"93d27560-188f-462f-8903-31696e650f3d","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null,"stop_details":null,"container":null},"usage":{"output_tokens":58}},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"bffa141e-0404-4a0e-8615-18e427c1e680","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"stream_event","event":{"type":"message_stop"},"session_id":"75a8ea39-878f-446a-a466-a09657aac33f","parent_tool_use_id":null,"uuid":"e3704326-a773-41e2-b81c-6d084255d184","api_message_id":"msg_011Ch2K2i5zHi2HJFFQy5ZZL"}
        {"type":"result","subtype":"success","is_error":false,"duration_ms":4076,"num_turns":2,"result":"AutoCAD isn't connected: no running AutoCAD Architecture 2027 with the AI Copilot add-in was found. Please start AutoCAD Architecture 2027 so the add-in loads, then ask me again.","stop_reason":"end_turn","session_id":"75a8ea39-878f-446a-a466-a09657aac33f","total_cost_usd":0.0011406300000000001,"usage":{"input_tokens":4,"cache_creation_input_tokens":5217,"cache_read_input_tokens":5083,"output_tokens":92},"modelUsage":{"claude-haiku-5-5":{"inputTokens":4,"outputTokens":92}},"permission_denials":[],"terminal_reason":"completed","uuid":"4f6483fe-22d0-413c-9865-b4df2676b18e"}
        """;

    /// <summary>Claude Code with no login.</summary>
    public const string ClaudeSignedOut =
        """
        {"type":"system","subtype":"init","cwd":"C:/Users/u/AppData/Local/AcadCopilot/workspace/claude","session_id":"62aef8df-55e8-469c-86d4-fef7dcf0026a","tools":[],"mcp_servers":[],"model":"claude-opus-5-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.312","uuid":"deba3c45-48d0-4c93-b9a6-3205d42abc9b"}
        {"type":"system","subtype":"status","status":"requesting","session_id":"62aef8df-55e8-469c-86d4-fef7dcf0026a","uuid":"b4ca63ca-3148-40e7-85ab-6f77c5861424"}
        {"type":"assistant","message":{"id":"63bef0a8-45f0-44fa-9435-3ef189b4301e","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"parent_tool_use_id":null,"session_id":"62aef8df-55e8-469c-86d4-fef7dcf0026a","uuid":"6026c42b-e0fe-4a03-b5b6-38750eef584b","timestamp":"2026-11-14T06:11:34.062Z","error":"authentication_failed","is_api_error_message":true}
        {"type":"result","subtype":"success","is_error":true,"duration_ms":412,"num_turns":1,"result":"Not logged in · Please run /login","stop_reason":"stop_sequence","session_id":"62aef8df-55e8-469c-86d4-fef7dcf0026a","total_cost_usd":0,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},"modelUsage":{},"permission_denials":[],"terminal_reason":"api_error","uuid":"df5fea37-9c2a-4934-9026-81cdf0398a32"}
        """;

    /// <summary>
    /// Claude Code: --resume of a session whose last turn the user stopped. Claude Code first writes its synthetic
    /// "No response requested." placeholder for the cut-off turn (seen in the first real run, model "&lt;synthetic&gt;",
    /// before the new prompt in the session transcript) and, in the worst case, a result for it; only then the real
    /// answer to the prompt on stdin follows. Shapes from the 2.1.325 transcript of that run.
    /// </summary>
    public const string ClaudeResumeAfterStop =
        """
        {"type":"system","subtype":"init","cwd":"C:/Users/u/AppData/Local/AcadCopilot/workspace/claude","session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","tools":["mcp__acad__run_commands"],"mcp_servers":[{"name":"acad","status":"connected","source":"dynamic"}],"model":"claude-opus-5-5","permissionMode":"dontAsk","apiKeySource":"none","claude_code_version":"2.1.325","uuid":"a1"}
        {"type":"assistant","message":{"id":"fc6af830-70d3-4e7b-81e0-2bc6c44037e5","container":null,"model":"<synthetic>","role":"assistant","stop_details":null,"stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"No response requested."}],"context_management":null},"parent_tool_use_id":null,"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","uuid":"4bd9e8e9-80c9-4652-b1aa-1f0d0de134b0"}
        {"type":"result","subtype":"success","is_error":false,"duration_ms":3,"num_turns":0,"result":"No response requested.","stop_reason":"stop_sequence","session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},"modelUsage":{}}
        {"type":"system","subtype":"status","status":"requesting","session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","uuid":"a2"}
        {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-5-5","id":"msg_1","type":"message","role":"assistant","content":[]}},"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","parent_tool_use_id":null,"uuid":"a3"}
        {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}},"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","parent_tool_use_id":null,"uuid":"a4"}
        {"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}},"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","parent_tool_use_id":null,"uuid":"a5"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"The plot is drawn"}},"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","parent_tool_use_id":null,"uuid":"a6"}
        {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"; the walls come next."}},"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","parent_tool_use_id":null,"uuid":"a7"}
        {"type":"assistant","message":{"model":"claude-opus-5-5","id":"msg_1","type":"message","role":"assistant","content":[{"type":"thinking","thinking":"","signature":"x"},{"type":"text","text":"The plot is drawn; the walls come next."}]},"parent_tool_use_id":null,"session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","uuid":"a8"}
        {"type":"result","subtype":"success","is_error":false,"duration_ms":81000,"num_turns":1,"result":"The plot is drawn; the walls come next.","stop_reason":"end_turn","session_id":"3d14789e-7bd9-4b56-afdd-cf3e4c310175","usage":{"input_tokens":6,"cache_creation_input_tokens":100,"cache_read_input_tokens":900,"output_tokens":40},"modelUsage":{"claude-opus-5-5":{"inputTokens":6,"outputTokens":40}}}
        """;

    /// <summary>Claude Code: --resume with an unknown session id.</summary>
    public const string ClaudeMissingSession =
        """
        {"type":"result","subtype":"error_during_execution","duration_ms":0,"duration_api_ms":0,"is_error":true,"num_turns":0,"stop_reason":null,"session_id":"00000000-0000-4000-8000-000000000000","total_cost_usd":0,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},"modelUsage":{},"permission_denials":[],"errors":["No conversation found with session ID: 00000000-0000-4000-8000-000000000000"]}
        """;

    /// <summary>Codex CLI: acad_status call through -c mcp_servers overrides.</summary>
    public const string CodexToolCallTurn =
        """
        {"type":"thread.started","thread_id":"01a1d8a9-93d6-7931-9de5-6270d894d90d"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I’ll call acad_status once."}}
        {"type":"item.started","item":{"id":"item_1","type":"mcp_tool_call","server":"acad","tool":"acad_status","arguments":{},"result":null,"error":null,"status":"in_progress"}}
        {"type":"item.completed","item":{"id":"item_1","type":"mcp_tool_call","server":"acad","tool":"acad_status","arguments":{},"result":{"content":[{"type":"text","text":"{\"connected\":false,\"error\":\"No running AutoCAD Architecture 2027 with the AI Copilot add-in was found. Ask the user to start AutoCAD Architecture 2027 (the add-in loads automatically) and try again.\",\"panelTools\":true}"}],"structured_content":null},"error":null,"status":"completed"}}
        {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"OK"}}
        {"type":"turn.completed","usage":{"input_tokens":30403,"cached_input_tokens":14976,"cache_write_input_tokens":0,"output_tokens":76,"reasoning_output_tokens":0}}
        """;

    /// <summary>Codex CLI: exec resume of the same thread.</summary>
    public const string CodexResumeTurn =
        """
        {"type":"thread.started","thread_id":"01a1d8a9-93d6-7931-9de5-6270d894d90d"}
        {"type":"turn.started"}
        {"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"OK"}}
        {"type":"turn.completed","usage":{"input_tokens":45702,"cached_input_tokens":30080,"cache_write_input_tokens":0,"output_tokens":81,"reasoning_output_tokens":0}}
        """;

    /// <summary>Codex CLI with an empty CODEX_HOME (first retries and the final failure).</summary>
    public const string CodexSignedOut =
        """
        {"type":"thread.started","thread_id":"01a1d8ae-f99d-7653-9d8b-8d64d4c52139"}
        {"type":"turn.started"}
        {"type":"error","message":"Reconnecting... 2/5 (unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: wss://api.openai.com/v1/responses, cf-ray: a5a3dbed2cc0a7a6-DEL)"}
        {"type":"error","message":"Reconnecting... 3/5 (unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: wss://api.openai.com/v1/responses, cf-ray: a5a3dbf08c2da72b-DEL)"}
        {"type":"error","message":"Reconnecting... 4/5 (unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: wss://api.openai.com/v1/responses, cf-ray: a5a3dbf63e0c3ebe-DEL)"}
        {"type":"error","message":"unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses, cf-ray: a5a3dc49bbf6a7bb-DEL, request id: req_d8ce2b4d8c8e40d29e4edff1d21cf43e"}
        {"type":"turn.failed","error":{"message":"unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses, cf-ray: a5a3dc49bbf6a7bb-DEL, request id: req_d8ce2b4d8c8e40d29e4edff1d21cf43e"}}
        """;

    /// <summary>Gemini CLI set to an API key that the API rejects (real output).</summary>
    public const string GeminiApiKeyRejected =
        """
        {"type":"init","timestamp":"2026-11-14T06:12:23.382Z","session_id":"7a3460dc-ef68-4cad-a642-fb5f7a27f678","model":"auto"}
        {"type":"message","timestamp":"2026-11-14T06:12:23.385Z","role":"user","content":"hi"}
        {"type":"result","timestamp":"2026-11-14T06:12:24.499Z","status":"error","error":{"type":"unknown","message":"[API Error: {\"error\":{\"message\":\"{\\n  \\\"error\\\": {\\n    \\\"code\\\": 401,\\n    \\\"message\\\": \\\"API key not valid. Please pass a valid API key.\\\",\\n    \\\"status\\\": \\\"UNAUTHENTICATED\\\",\\n    \\\"details\\\": [\\n      {\\n        \\\"@type\\\": \\\"type.googleapis.com/google.rpc.ErrorInfo\\\",\\n        \\\"reason\\\": \\\"ACCESS_TOKEN_TYPE_UNSUPPORTED\\\",\\n        \\\"metadata\\\": {\\n          \\\"method\\\": \\\"google.ai.generativelanguage.v1beta.GenerativeService.StreamGenerateContent\\\",\\n          \\\"service\\\": \\\"generativelanguage.googleapis.com\\\"\\n        }\\n      }\\n    ]\\n  }\\n}\\n\",\"code\":401,\"status\":\"Unauthorized\"}}]"},"stats":{"total_tokens":0,"input_tokens":0,"output_tokens":0,"cached":0,"input":0,"duration_ms":0,"tool_calls":0,"models":{"gemini-3.1-flash-lite":{"total_tokens":0,"input_tokens":0,"output_tokens":0,"cached":0,"input":0},"gemini-3.1-pro-preview":{"total_tokens":0,"input_tokens":0,"output_tokens":0,"cached":0,"input":0}}}}
        """;
}
