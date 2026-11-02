(function () {

  /**
   * Decimal adjustment of a number.
   *
   * @param   {String}    type    The type of adjustment.
   * @param   {Number}    value   The number.
   * @param   {Integer}   exp     The exponent (the 10 logarithm of the adjustment base).
   * @returns {Number}            The adjusted value.
   */
  function decimalAdjust(type, value, exp) {
    // If the exp is undefined or zero...
    if (typeof exp === 'undefined' || +exp === 0) {
      return Math[type](value);
    }
    value = +value;
    exp = +exp;
    // If the value is not a number or the exp is not an integer...
    if (isNaN(value) || !(typeof exp === 'number' && exp % 1 === 0)) {
      return NaN;
    }
    // Shift
    value = value.toString().split('e');
    value = Math[type](+(value[0] + 'e' + (value[1] ? (+value[1] - exp) : -exp)));
    // Shift back
    value = value.toString().split('e');
    return +(value[0] + 'e' + (value[1] ? (+value[1] + exp) : exp));
  }

  // Decimal round
  if (!Math.round10) {
    Math.round10 = function (value, exp) {
      return decimalAdjust('round', value, exp);
    };
  }
  // Decimal floor
  if (!Math.floor10) {
    Math.floor10 = function (value, exp) {
      return decimalAdjust('floor', value, exp);
    };
  }
  // Decimal ceil
  if (!Math.ceil10) {
    Math.ceil10 = function (value, exp) {
      return decimalAdjust('ceil', value, exp);
    };
  }
})();

function isFloat(n) {
  return Number(n) === n && n % 1 !== 0;
}

function cleanupOrphanedTooltips() {
  $('[role="tooltip"]').each(function () {
    var tooltipId = this.id;

    if (!tooltipId || $('[aria-describedby="' + tooltipId + '"]').length === 0) {
      $(this).remove();
    }
  });
}

function ensurePersistentTooltip(element) {
  var $element = $(element);
  var tooltip = $element.data('bs.tooltip');

  if (!tooltip || tooltip.config.trigger !== 'manual') {
    if (tooltip) {
      $element.tooltip('dispose');
    }

    $element.tooltip({
      trigger: 'manual',
      delay: 0
    });
  }
}

if ($.fn.tooltip && $.fn.tooltip.Constructor && $.fn.tooltip.Constructor.Default) {
  $.fn.tooltip.Constructor.Default.trigger = 'manual';
  $.fn.tooltip.Constructor.Default.delay = 0;
}

$(document)
  .on('mouseenter', '[data-toggle="tooltip"]', function () {
    ensurePersistentTooltip(this);
    $(this).data('ptmagic-tooltip-hover', true).tooltip('show');
  })
  .on('mouseleave', '[data-toggle="tooltip"]', function () {
    $(this).data('ptmagic-tooltip-hover', false);

    if (!$(this).data('ptmagic-tooltip-focus')) {
      $(this).tooltip('hide');
    }
  })
  .on('focusin', '[data-toggle="tooltip"]', function () {
    ensurePersistentTooltip(this);
    $(this).data('ptmagic-tooltip-focus', true).tooltip('show');
  })
  .on('focusout', '[data-toggle="tooltip"]', function () {
    $(this).data('ptmagic-tooltip-focus', false);

    if (!$(this).data('ptmagic-tooltip-hover')) {
      $(this).tooltip('hide');
    }
  });

$.fn.autocolor = function (isSawTableCell = false) {
  return this.each(function (index) {
    var element = $(this);

    if (isSawTableCell) {
      element = $(".tablesaw-cell-content", this);
    }

    var text = $(element).text().replace(/[^\d.,-]/g, '');
    var floatVal = parseFloat(text);

    element.removeClass('text-danger');
    element.removeClass('text-success');
    if (floatVal > 0) {
      element.addClass('text-success');
    } else {
      element.addClass('text-danger');
    }
  });
  
};