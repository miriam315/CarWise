-- Run with the database owner in Supabase SQL Editor (or applied automatically to local PostgreSQL).
-- Reference data and payroll state are server-only; browsers never receive the service-role key.
CREATE TABLE IF NOT EXISTS public.carwise_rules (id text PRIMARY KEY, document jsonb NOT NULL);
CREATE TABLE IF NOT EXISTS public.carwise_cars (id text PRIMARY KEY, document jsonb NOT NULL);
CREATE TABLE IF NOT EXISTS public.carwise_occupations (id text PRIMARY KEY, document jsonb NOT NULL);
CREATE TABLE IF NOT EXISTS public.carwise_employees (id text PRIMARY KEY, document jsonb NOT NULL);
CREATE TABLE IF NOT EXISTS public.carwise_official (
 id text PRIMARY KEY, document jsonb NOT NULL,
 tax_year int GENERATED ALWAYS AS ((document->>'שנת מס')::int) STORED,
 registration_year int GENERATED ALWAYS AS ((document->>'שנת רישום')::int) STORED,
 vehicle_type text GENERATED ALWAYS AS (document->>'קוד סוג רכב') STORED,
 maker_code text GENERATED ALWAYS AS (document->>'קוד תוצר') STORED,
 model_code text GENERATED ALWAYS AS (document->>'קוד דגם') STORED,
 UNIQUE (tax_year, registration_year, vehicle_type, maker_code, model_code)
);
CREATE TABLE IF NOT EXISTS public.carwise_conversations (
 employee_id text PRIMARY KEY, employee jsonb NOT NULL,
 status text NOT NULL CHECK(status IN ('Active','PendingAgentResponse')),
 target text, updated timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS public.carwise_messages (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, employee_id text NOT NULL,
 role text NOT NULL CHECK(role IN ('user','assistant')), text text NOT NULL,
 created timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS carwise_messages_employee ON public.carwise_messages(employee_id,id);
CREATE TABLE IF NOT EXISTS public.carwise_tickets (
 id uuid PRIMARY KEY DEFAULT gen_random_uuid(), employee_id text NOT NULL, snapshot jsonb NOT NULL,
 target text NOT NULL REFERENCES public.carwise_cars(id), question text NOT NULL,
 status text NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','resolved')),
 answer text, participation numeric CHECK(participation BETWEEN 0 AND 20000),
 created timestamptz NOT NULL DEFAULT now(), resolved_at timestamptz,
 CHECK ((status='pending' AND answer IS NULL AND participation IS NULL) OR
        (status='resolved' AND answer IS NOT NULL AND participation IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS carwise_one_pending_ticket ON public.carwise_tickets(employee_id,target) WHERE status='pending';

CREATE OR REPLACE FUNCTION public.carwise_create_ticket(p_employee jsonb,p_target text,p_question text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE result carwise_tickets; existing boolean;
BEGIN
 PERFORM pg_advisory_xact_lock(hashtextextended((p_employee->>'id')||':'||p_target,0));
 SELECT * INTO result FROM carwise_tickets WHERE employee_id=p_employee->>'id' AND target=p_target AND status='pending';
 existing := FOUND;
 IF NOT existing THEN
  INSERT INTO carwise_tickets(employee_id,snapshot,target,question)
  VALUES(p_employee->>'id',p_employee,p_target,p_question) RETURNING * INTO result;
  INSERT INTO carwise_messages(employee_id,role,text) VALUES(p_employee->>'id','assistant',
   'הפנייה הועברה לנציג. החלופה ממתינה לאישור השתתפות; כשתתקבל תשובה נמשיך מאותה נקודה.');
 END IF;
 INSERT INTO carwise_conversations(employee_id,employee,status,target)
 VALUES(p_employee->>'id',p_employee,'PendingAgentResponse',p_target)
 ON CONFLICT(employee_id) DO UPDATE SET employee=excluded.employee,status=excluded.status,target=excluded.target,updated=now();
 RETURN to_jsonb(result);
END $$;

CREATE OR REPLACE FUNCTION public.carwise_resolve_ticket(p_id uuid,p_participation numeric,p_answer text,p_message text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE result carwise_tickets; next_target text;
BEGIN
 IF p_participation NOT BETWEEN 0 AND 20000 OR length(trim(p_answer))=0 OR length(p_answer)>2000 THEN
  RAISE EXCEPTION 'Invalid agent answer' USING ERRCODE='22023';
 END IF;
 SELECT * INTO result FROM carwise_tickets WHERE id=p_id FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown ticket' USING ERRCODE='22023'; END IF;
 IF result.status='resolved' THEN RETURN to_jsonb(result); END IF;
 UPDATE carwise_tickets SET status='resolved',answer=p_answer,participation=p_participation,resolved_at=now()
 WHERE id=p_id RETURNING * INTO result;
 INSERT INTO carwise_messages(employee_id,role,text) VALUES(result.employee_id,'assistant',p_message);
 SELECT target INTO next_target FROM carwise_tickets WHERE employee_id=result.employee_id AND status='pending' ORDER BY created LIMIT 1;
 UPDATE carwise_conversations SET status=CASE WHEN next_target IS NULL THEN 'Active' ELSE 'PendingAgentResponse' END,
 target=next_target,updated=now() WHERE employee_id=result.employee_id;
 RETURN to_jsonb(result);
END $$;

CREATE OR REPLACE FUNCTION public.carwise_save_conversation(p_employee jsonb,p_target text)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=public,pg_temp AS $$
DECLARE waiting text;
BEGIN
 SELECT target INTO waiting FROM carwise_tickets WHERE employee_id=p_employee->>'id' AND status='pending' ORDER BY created LIMIT 1;
 INSERT INTO carwise_conversations(employee_id,employee,status,target)
 VALUES(p_employee->>'id',p_employee,CASE WHEN waiting IS NULL THEN 'Active' ELSE 'PendingAgentResponse' END,coalesce(waiting,p_target))
 ON CONFLICT(employee_id) DO UPDATE SET employee=excluded.employee,status=excluded.status,target=excluded.target,updated=now();
 RETURN '{}'::jsonb;
END $$;

DO $$
DECLARE t text; role_name text;
BEGIN
 FOREACH t IN ARRAY ARRAY['carwise_rules','carwise_cars','carwise_occupations','carwise_employees','carwise_official',
  'carwise_conversations','carwise_messages','carwise_tickets'] LOOP
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('REVOKE ALL ON public.%I FROM PUBLIC',t);
  FOREACH role_name IN ARRAY ARRAY['anon','authenticated'] LOOP
   IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=role_name) THEN
    EXECUTE format('REVOKE ALL ON public.%I FROM %I',t,role_name);
   END IF;
  END LOOP;
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='service_role') THEN
   EXECUTE format('GRANT ALL ON public.%I TO service_role',t);
  END IF;
 END LOOP;
 REVOKE ALL ON FUNCTION public.carwise_create_ticket(jsonb,text,text) FROM PUBLIC;
 REVOKE ALL ON FUNCTION public.carwise_resolve_ticket(uuid,numeric,text,text) FROM PUBLIC;
 REVOKE ALL ON FUNCTION public.carwise_save_conversation(jsonb,text) FROM PUBLIC;
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='service_role') THEN
  GRANT USAGE, SELECT ON SEQUENCE public.carwise_messages_id_seq TO service_role;
  GRANT EXECUTE ON FUNCTION public.carwise_create_ticket(jsonb,text,text) TO service_role;
  GRANT EXECUTE ON FUNCTION public.carwise_resolve_ticket(uuid,numeric,text,text) TO service_role;
  GRANT EXECUTE ON FUNCTION public.carwise_save_conversation(jsonb,text) TO service_role;
 END IF;
END $$;
