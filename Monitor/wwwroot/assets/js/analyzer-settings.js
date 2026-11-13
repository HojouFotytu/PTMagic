// Identities belong to form rows, never to a MarketTrend or metric name.
var checkedMTButtons = false, checkedGSButtons = false, checkedSMSButtons = false;
var statusMarketTrends = {}, statusGlobalSettings = {}, statusSingleMarketSettings = {};
var analyzerRowSequence = 0;
function newAnalyzerRowId() { return 'new_' + Date.now().toString(36) + '_' + (++analyzerRowSequence); }
function analyzerAttribute(value) {
  return String(value == null ? '' : value).replace(/&/g, '&amp;').replace(/"/g, '&quot;')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/'/g, '&#39;');
}
function analyzerInput(prefix, field, value, caption, width, placeholder) {
  return '<div class="col-md-' + width + '"><input type="text" class="form-control" name="' +
    analyzerAttribute(prefix + field) + '" value="' + analyzerAttribute(value) + '" placeholder="' +
    analyzerAttribute(placeholder || caption) + '"><span class="help-block"><small>' +
    analyzerAttribute(caption) + '</small></span></div>';
}
function syncAnalyzerTrendOptions() {
  var trends = [];
  $('[data-analyzer-trend-name]:enabled').each(function () {
    trends.push({ token: $(this).attr('data-trend-token'), name: $(this).val(), original: $(this).attr('data-original-name') });
  });
  $('[data-analyzer-markettrend]').each(function () {
    var select = $(this), previous = select.val() || '';
    var token = select.find(':selected').attr('data-trend-token');
    if (!token && previous) {
      var match = trends.find(function (trend) { return trend.original.toLowerCase() === previous.toLowerCase(); });
      if (match) token = match.token;
    }
    select.empty();
    var found = false;
    trends.forEach(function (trend) {
      var selected = trend.token === token;
      var value = selected && trend.name === trend.original && trend.name.toLowerCase() === previous.toLowerCase() ? previous : trend.name;
      select.append($('<option>').attr('data-trend-token', trend.token).val(value).text(trend.name).prop('selected', selected));
      found = found || selected;
    });
    if (!found && previous) select.prepend($('<option>').val(previous).text(previous + ' (reference needs attention)').prop('selected', true));
    else if (!found && trends.length) select.prop('selectedIndex', 0);
  });
}
$(document).on('input.analyzer change.analyzer', '[data-analyzer-trend-name]', syncAnalyzerTrendOptions);
function AnalyzerTriggerTemplate(row) {
  var rowId = row.rowId || newAnalyzerRowId();
  var triggerPrefix = row.triggerPrefix || '';
  var parent = 'MarketAnalyzer_' + row.settingType + '_' + row.settingName + '|';
  var prefix = parent + triggerPrefix + 'Trigger_' + rowId + '|';
  var kind = row.kind || 'metric';
  var result = '<div class="form-group row analyzer-editor-row">' +
    '<input type="hidden" name="' + analyzerAttribute(parent + triggerPrefix + 'TriggerOrder') +
    '" value="' + analyzerAttribute(rowId) + '">' +
    '<input type="hidden" name="' + analyzerAttribute(prefix + 'Kind') + '" value="' + kind + '">';
  if (kind === 'metric') {
    result += '<div class="col-md-4"><select data-analyzer-markettrend="true" name="' + analyzerAttribute(prefix + 'MarketTrendName') +
      '" class="form-control">' + row.marketTrends + '</select><span class="help-block"><small>MarketTrend</small></span></div>';
    result += analyzerInput(prefix, 'Metric', row.metric, 'Metric (blank = PriceTrend)', 4, 'PriceTrend');
    result += analyzerInput(prefix, 'Tag', row.tag, 'Tag for Boolean expression', 4, 'Optional for AND / OR');
    if (row.settingType === 'SingleMarketSetting') {
      result += '<div class="col-md-4"><select name="' + analyzerAttribute(prefix + 'MarketTrendRelation') +
        '" class="form-control">' + row.marketTrendRelations + '</select><span class="help-block"><small>Relation (PriceTrend only)</small></span></div>';
    }
    result += analyzerInput(prefix, 'MinChange', row.minChange, 'Minimum (metric units)', 3);
    result += analyzerInput(prefix, 'MaxChange', row.maxChange, 'Maximum (metric units)', 3);
  } else if (kind === 'volume') {
    result += '<div class="col-md-4"><label class="col-form-label">24h Volume in ' +
      analyzerAttribute(row.mainMarket) + '</label></div>';
    result += analyzerInput(prefix, 'Min24hVolume', row.min24hVolume, 'Minimum 24h volume', 3);
    result += analyzerInput(prefix, 'Max24hVolume', row.max24hVolume, 'Maximum 24h volume', 3);
    result += analyzerInput(prefix, 'Tag', row.tag, 'Tag', 4);
  } else if (kind === 'age') {
    result += analyzerInput(prefix, 'AgeDaysLowerThan', row.coinAgeDays, 'Coin age lower than (days)', 6);
    result += analyzerInput(prefix, 'Tag', row.tag, 'Tag', 4);
  } else if (kind === 'hours') {
    result += analyzerInput(prefix, 'HoursSinceTriggered', row.hoursSinceTriggered, 'Hours since triggered', 6);
    result += analyzerInput(prefix, 'Tag', row.tag, 'Tag', 4);
  }
  return result + '<div class="col-md-2"><button type="button" class="btn btn-danger btn-custom btn-block text-uppercase waves-effect waves-light btn-remove-parentrow-' +
    analyzerAttribute(row.settingType + '-' + row.settingName) + '">Remove</button></div></div>';
}
const GSStandardTriggerTemplate = row => AnalyzerTriggerTemplate(Object.assign({ kind: 'metric' }, row));
const SMSStandardTriggerTemplate = row => AnalyzerTriggerTemplate(Object.assign({ kind: 'metric' }, row));
const CoinAgeTriggerTemplate = row => AnalyzerTriggerTemplate(Object.assign({ kind: 'age' }, row));
const SMS24hVolumeTriggerTemplate = row => AnalyzerTriggerTemplate(Object.assign({ kind: 'volume' }, row));
const SMSHoursActiveTriggerTemplate = row => AnalyzerTriggerTemplate(Object.assign({ kind: 'hours', triggerPrefix: 'Off' }, row));

function PropertyTemplate(row) {
  var rowId = row.rowId || newAnalyzerRowId();
  var prefix = 'MarketAnalyzer_' + row.settingType + '_' + row.settingName + '|' + row.propertyType + 'Property_' + rowId;
  return '<div class="form-group row analyzer-editor-row">' +
    analyzerInput('', prefix, row.propertyKeySimple, 'ProfitTrailer property', 4) +
    analyzerInput('', prefix + '|Value', row.value, 'Value', 3) +
    '<div class="col-md-3"><select name="' + analyzerAttribute(prefix + '|ValueMode') + '" class="form-control">' +
    row.valueModes + '</select><span class="help-block"><small>Value mode</small></span></div>' +
    '<div class="col-md-2"><button type="button" class="btn btn-danger btn-custom btn-block text-uppercase waves-effect waves-light btn-remove-parentrow-' +
    analyzerAttribute(row.settingType + '-' + row.settingName) + '">Remove</button></div></div>';
}

function checkMTMoveButtons() {
  if (!checkedMTButtons) {
    $('.btn-move-MT').each(function (index) {
      var dataTarget = $(this).data('datatarget');
      var dataDirection = $(this).data('datadirection');

      var currentElement = $('#' + dataTarget).closest('.settings-markettrend');

      if (dataDirection === 'up') {
        if ($(currentElement).is('.settings-markettrend:first-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');
        }
      } else if (dataDirection === 'down') {
        if ($(currentElement).is('.settings-markettrend:last-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');
        }
      }
    });
  }
  checkedMTButtons = true;
}

function checkGSMoveButtons() {
  if (!checkedGSButtons) {
    $('.btn-move-GS').each(function (index) {
      var dataTarget = $(this).data('datatarget');
      var dataDirection = $(this).data('datadirection');

      var currentElement = $('#' + dataTarget).closest('.settings-globalsetting');

      if (dataDirection === 'up') {
        if ($(currentElement).is('.settings-globalsetting:first-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');
        }
      } else if (dataDirection === 'down') {
        if ($(currentElement).is('.settings-globalsetting:last-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');

          // Get next element
          var nextElement = $(currentElement).next('.settings-globalsetting').find('div');
          var nextElementId = $(nextElement).attr('id').toLowerCase().replace('globalsetting_', '');
          if (nextElementId.startsWith('default')) {
            // Hide "Move Down" button when the next setting is the default setting
            $(this).addClass('hidden');
          }
        }
      }
    });
  }
  checkedGSButtons = true;
}

function checkSMSMoveButtons() {
  if (!checkedSMSButtons) {
    $('.btn-move-SMS').each(function (index) {
      var dataTarget = $(this).data('datatarget');
      var dataDirection = $(this).data('datadirection');

      var currentElement = $('#' + dataTarget).closest('.settings-singlemarketsetting');

      if (dataDirection === 'up') {
        if ($(currentElement).is('.settings-singlemarketsetting:first-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');
        }
      } else if (dataDirection === 'down') {
        if ($(currentElement).is('.settings-singlemarketsetting:last-child')) {
          $(this).addClass('hidden');
        } else {
          $(this).removeClass('hidden');
        }
      }
    });
  }
  checkedSMSButtons = true;
}

// Check only rows still present in the editor. A removed row must not leave a
// stale, incomplete AJAX status that permanently prevents subsequent saves.
function analyzerSectionsReady(selector, statuses, inputSelector) {
  var ready = true;
  $(selector).each(function () {
    var section = $(this);
    var loadKey = section.attr('data-analyzer-load-key');
    if (!loadKey || statuses[loadKey] !== true || section.find(inputSelector).length !== 1) {
      ready = false;
    }
  });
  return ready;
}

function checkBuildStatus() {
  var buildCompleted =
    analyzerSectionsReady('.settings-markettrend', statusMarketTrends, 'input[data-analyzer-trend-name]') &&
    analyzerSectionsReady('.settings-globalsetting', statusGlobalSettings, 'input[name$="|SettingName"]') &&
    analyzerSectionsReady('.settings-singlemarketsetting', statusSingleMarketSettings, 'input[name$="|SettingName"]');

  $('#AnalyzerSettingsReady').val(buildCompleted ? 'true' : 'false');
  if (buildCompleted) {
    $('#div-loading-settings').addClass('hidden');
    $('#div-save-settings').removeClass('hidden');

    checkMTMoveButtons();
    checkGSMoveButtons();
    checkSMSMoveButtons();
  } else {
    $('#div-save-settings').addClass('hidden');
    $('#div-loading-settings').removeClass('hidden');
  }
  return buildCompleted;
}

$.fn.buildMarketTrendSettings = function () {
  return this.each(function (index) {
    var element = $(this);
    var marketTrendName = $(this).data('trendname');
    var rootUrl = $(this).data('rooturl');

    var loadKey = marketTrendName || newAnalyzerRowId();
    $(this).attr('data-analyzer-load-key', loadKey);
    statusMarketTrends[loadKey] = false;
    $('#AnalyzerSettingsReady').val('false');
    $('#div-save-settings').addClass('hidden');
    $('#div-loading-settings').removeClass('hidden');

    $(this).load(rootUrl + '_get/SettingsMarketTrends?mt=' + encodeURIComponent(marketTrendName) + '&newid=' + encodeURIComponent(loadKey), '', function (responseText, textStatus, XMLHttpRequest) {
      if (textStatus === 'error') {
        $.Notification.notify('error', 'top left', 'Build MarketTrendSettings failed!', 'PTMagic Monitor failed to build MarketTrendSettings.')
      } else {
        statusMarketTrends[loadKey] = true;
        syncAnalyzerTrendOptions();
        checkBuildStatus();

        $(this).removeClass('new');
        $('[data-toggle="tooltip"]').tooltip();
        var elems = Array.prototype.slice.call(document.querySelectorAll('[data-plugin="switchery"][data-switchery="false"]'));
        elems.forEach(function (html) {
          var switchery = new Switchery(html, { size : 'small' });
        });
      }
    });
  });
};

$.fn.buildGlobalSettings = function () {
  return this.each(function (index) {
    var element = $(this);
    var settingName = $(this).data('settingname');
    var rootUrl = $(this).data('rooturl');

    var loadKey = settingName || newAnalyzerRowId();
    $(this).attr('data-analyzer-load-key', loadKey);
    statusGlobalSettings[loadKey] = false;
    $('#AnalyzerSettingsReady').val('false');
    $('#div-save-settings').addClass('hidden');
    $('#div-loading-settings').removeClass('hidden');

    $(this).load(rootUrl + '_get/SettingsGlobalSettings?gs=' + encodeURIComponent(settingName) + '&newid=' + encodeURIComponent(loadKey), '', function (responseText, textStatus, XMLHttpRequest) {
      if (textStatus === 'error') {
        $.Notification.notify('error', 'top left', 'Build GlobalSettings failed!', 'PTMagic Monitor failed to build GlobalSettings.')
      } else {
        statusGlobalSettings[loadKey] = true;
        checkBuildStatus();

        $(this).removeClass('new');
        $('[data-toggle="tooltip"]').tooltip();
        var elems = Array.prototype.slice.call(document.querySelectorAll('[data-plugin="switchery"][data-switchery="false"]'));
        elems.forEach(function (html) {
          var switchery = new Switchery(html, { size: 'small' });
        });
      }
    });
  });
};

$.fn.buildSingleMarketSettings = function () {
  return this.each(function (index) {
    var element = $(this);
    var settingName = $(this).data('settingname');
    var rootUrl = $(this).data('rooturl');

    var loadKey = settingName || newAnalyzerRowId();
    $(this).attr('data-analyzer-load-key', loadKey);
    statusSingleMarketSettings[loadKey] = false;
    $('#AnalyzerSettingsReady').val('false');
    $('#div-save-settings').addClass('hidden');
    $('#div-loading-settings').removeClass('hidden');

    $(this).load(rootUrl + '_get/SettingsSingleMarketSettings?gs=' + encodeURIComponent(settingName) + '&newid=' + encodeURIComponent(loadKey), '', function (responseText, textStatus, XMLHttpRequest) {
      if (textStatus === 'error') {
        $.Notification.notify('error', 'top left', 'Build SingleMarketSettings failed!', 'PTMagic Monitor failed to build SingleMarketSettings.')
      } else {
        statusSingleMarketSettings[loadKey] = true;
        checkBuildStatus();

        $(this).removeClass('new');
        $('[data-toggle="tooltip"]').tooltip();
        var elems = Array.prototype.slice.call(document.querySelectorAll('[data-plugin="switchery"][data-switchery="false"]'));
        elems.forEach(function (html) {
          var switchery = new Switchery(html, { size: 'small' });
        });
      }
    });
  });
};
