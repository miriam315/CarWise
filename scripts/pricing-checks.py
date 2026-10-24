"""Reference pricing only: public catalogue UI, exact mappings and unchanged inference routes."""
from copy import deepcopy
from datetime import datetime,timezone
import os
from playwright.sync_api import expect


def check(query,page,nav,screenshots):
    original=query('/admin/configuration');draft=deepcopy(original['configuration']);ai=draft['ai']
    direct=deepcopy(ai['extraction']);direct.update(provider='openai',baseUrl='https://api.openai.com/v1/',model='gpt-4.1-mini',customProvider=False)
    gemini={**direct,'provider':'gemini','baseUrl':'https://generativelanguage.googleapis.com/v1beta/','model':'gemini-2.5-flash'}
    missing={**direct,'model':'gpt-unknown-version'}
    ai.update(enabled=False,payslipEnabled=False,openRouterPricingEnabled=False,pricing=[],models=[
        {'id':'direct','name':'OpenAI direct','profile':direct},{'id':'gemini','name':'Gemini direct','profile':gemini},{'id':'missing','name':'Unmatched version','profile':missing}],
        serviceModels={'extraction':'direct','narration':'direct'},openRouterPriceMappings={})
    saved=query('/admin/configuration',{'configuration':draft,'expectedRevision':original['revision'],'note':''})
    policy=saved['policyRevision'];routes=deepcopy(saved['configuration']['ai']['models'])
    catalogue={'source':'https://openrouter.ai/api/v1/models','catalog':{'retrievedAt':'2026-10-24T10:00:00Z','models':{
        'openai/gpt-4.1-mini':{'inputUsdPerMillion':.4,'cachedInputUsdPerMillion':.1,'outputUsdPerMillion':1.6,'cacheWriteUsdPerMillion':None,'unavailableReason':None},
        'google/gemini-2.5-flash':{'inputUsdPerMillion':.3,'cachedInputUsdPerMillion':None,'outputUsdPerMillion':2.5,'cacheWriteUsdPerMillion':None,'unavailableReason':None}}},
        'lastAttempt':None,'error':None,'stale':False}
    fail=False;refreshes=[];errors=[]
    page.on('pageerror',lambda error:errors.append(str(error)))
    def mock(route):
        nonlocal catalogue
        if route.request.method=='POST':
            refreshes.append(route.request.url)
            if fail:catalogue={**catalogue,'error':'עדכון מחירון OpenRouter נכשל. המחירון האחרון נשאר פעיל.'}
            else:catalogue={**catalogue,'lastAttempt':datetime.now(timezone.utc).isoformat(),'error':None}
        route.fulfill(json=catalogue)
    page.route('**/api/admin/pricing/openrouter**',mock)
    page.reload();page.wait_for_selector('.vehicle-card');nav('ניהול המערכת')
    page.get_by_role('tab',name='עלויות AI ושער דולר',exact=True).click()
    panel=page.get_by_role('region',name='מחירון OpenRouter',exact=True)
    expect(panel).to_contain_text('ללא שינוי ספק ה־AI או ניתוב הקריאות')
    expect(panel.get_by_label('מזהה תמחור OpenRouter direct',exact=True)).to_have_attribute('placeholder','openai/gpt-4.1-mini')
    expect(panel.get_by_label('מזהה תמחור OpenRouter gemini',exact=True)).to_have_attribute('placeholder','google/gemini-2.5-flash')
    panel.get_by_label('סנכרון מחירי OpenRouter אוטומטי',exact=True).check()
    expect(panel).to_contain_text('אומדן לפי תעריף ייחוס OpenRouter')
    expect(panel).to_contain_text('לא נמצאה התאמה מדויקת במחירון')
    panel.get_by_label('מזהה תמחור OpenRouter missing',exact=True).fill('openai/gpt-4.1-mini')
    expect(panel).not_to_contain_text('לא נמצאה התאמה מדויקת במחירון')
    panel.get_by_label('שעת עדכון מחירון OpenRouter',exact=True).fill('8')
    panel.get_by_role('button',name='עדכון מחירי OpenRouter עכשיו',exact=True).click()
    expect(panel.get_by_role('button',name='עדכון מחירי OpenRouter עכשיו',exact=True)).to_be_enabled()
    assert refreshes and query('/admin/configuration')['revision']==saved['revision']
    fail=True;panel.get_by_role('button',name='עדכון מחירי OpenRouter עכשיו',exact=True).click()
    expect(panel.get_by_role('alert')).to_contain_text('המחירון האחרון נשאר פעיל')
    expect(panel.locator('tbody tr').first).to_contain_text('0.4')
    with page.expect_response(lambda r:r.request.method=='POST' and r.url.endswith('/api/admin/configuration')) as saving:
        page.get_by_role('button',name='שמירת הגדרות',exact=True).click()
    assert saving.value.ok
    persisted=query('/admin/configuration');actual=persisted['configuration']['ai']
    assert actual['models']==routes and persisted['policyRevision']==policy
    assert actual['openRouterPricingEnabled'] and actual['openRouterPricingUpdateHourUtc']==8
    assert actual['openRouterPriceMappings']['openai/api.openai.com/gpt-unknown-version']=='openai/gpt-4.1-mini'
    page.screenshot(path=str(screenshots/'openrouter-prices-desktop.png'),full_page=True)
    page.set_viewport_size({'width':390,'height':844});expect(panel).to_be_visible()
    assert page.evaluate('document.documentElement.scrollWidth<=innerWidth+1')
    page.screenshot(path=str(screenshots/'openrouter-prices-mobile.png'),full_page=True)
    assert not errors,errors
    page.unroute('**/api/admin/pricing/openrouter**',mock)
    if os.environ.get('CARWISE_VERIFY_OPENROUTER_LIVE')=='true':
        live=query('/admin/pricing/openrouter/refresh',{})
        assert not live['error'] and live['catalog'] and len(live['catalog']['models'])>10
        assert query('/admin/configuration')['revision']==persisted['revision']
        print('PASS: live public OpenRouter catalogue imported and persisted without credentials, paid calls or policy changes.',flush=True)
    print('PASS: reference prices for direct providers, exact explicit mapping, price source/date, missing-price state, last-good display, operational settings persistence, unchanged model routes and mobile.',flush=True)
