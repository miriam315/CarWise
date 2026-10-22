# Conversation workflow

The native model interprets natural language and chooses typed actions. `AdvisorWorkflow` owns the persisted goal, pending action, requested category, offered/rejected cars, preferences and a bounded stack of earlier options. Policy/profile detours preserve the comparison and any pending referral. A bare affirmation accepts only a recorded offer. A pause is reversible; restoring an option recalculates against the current engine and policy rather than reusing an old monetary result.

`AdvisorOperations` implements payroll updates, browsing, comparisons, personal contribution and representative requests independently of language interpretation. Tools no longer recursively invoke the entire chat handler. A turn is persisted once by the service. Explicit numerical replies and catalogue selections use the same operations directly; these shortcuts avoid unnecessary provider requests. Existing rule-based guidance remains available when AI is disabled or unavailable.

Each tool returns a structured status and a fresh workflow snapshot. Unknown information is not replaced by zero. Current-car contribution remains personal; a new-car catalogue default has explicit provenance. Model-generated prose cannot replace authoritative calculation values. Contextual narration receives the latest question, up to six recent messages with the employee name/ID removed, and the workflow; verified fact placeholders are still inserted by code. It cannot invent action confirmations or next-step offers. Free-text dialogue itself may contain personal details, so organization approval for the selected external provider remains necessary.

The workflow is stored in existing employee JSON; old snapshots migrate on read. Memory holds at most eight prior options and 24 action transitions, without copying payroll values or calculated net amounts. New chat clears it while retaining approved profile inputs and existing tickets.

## Verification

`npm test` covers typed actions, persisted conversation journeys, policy detours, rejected offers, returning to previous options, missing inputs and actual representative tickets. Scripted model tests verify the contract and state transitions; they do not establish the quality of a live provider's language understanding.

`npm run eval:conversation` runs an optional live-model journey using synthetic data and an in-memory database. It is skipped unless `CARWISE_EVAL_AI_KEY` is explicitly set in the process environment. Optional `CARWISE_EVAL_AI_PROVIDER` accepts `gemini` (default), `openai` or `anthropic`; `CARWISE_EVAL_AI_MODEL` selects the model. Standard official provider endpoints only; no production vault/settings are read. The evaluation uses paid API requests (maximum 24) and a five-minute cancellation budget. It emits a synthetic transcript for review. The evaluator sets its own output budget, without modifying production administrator settings. Run this deliberately in a secure terminal and remove the environment key afterward. Never commit keys.

Passing deterministic tests does not guarantee that every free-form phrase will be understood. A real-model evaluation and human review of concision and conversational quality are separate acceptance steps. The live journey uses the actual native provider adapters and workflow, with safe template narration to isolate tool selection from a second model.
